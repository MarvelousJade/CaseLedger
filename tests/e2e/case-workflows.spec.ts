import { createHash, randomUUID } from 'node:crypto'
import { expect, test, type Page } from '@playwright/test'
import { waitForVerificationQueuesToDrain } from './support/broker'
import {
  apiOutboxAttemptCount,
  replayApiVerificationRequest,
  replayWorkerVerificationResult,
  tamperWithFirstAuditEvent,
  workerInboxCount,
  workerResultPublishAttempts,
} from './support/database'
import { clearWebhookDeliveries, webhookDeliveries } from './support/webhook'

const analystEmail = 'analyst@caseledger.dev'
const analystPassword = 'Analyst123!'

async function signIn(page: Page) {
  await page.goto('/')
  await page.getByRole('textbox', { name: 'Email address' }).fill(analystEmail)
  await page.getByRole('textbox', { name: /^Password/ }).fill(analystPassword)
  await page.getByRole('button', { name: 'Sign in securely' }).click()

  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible()
  await expect(page.getByText('Avery Singh', { exact: true }).first()).toBeVisible()
}

async function createCase(page: Page, title: string) {
  await page.getByRole('button', { name: 'Cases', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Cases', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'New case' }).click()

  const dialog = page.getByRole('dialog', { name: 'Create a case' })
  await dialog.getByRole('textbox', { name: /Case title/i }).fill(title)
  await dialog.getByRole('textbox', { name: /Summary/i }).fill('Playwright verifies the complete case and evidence workflow against PostgreSQL.')
  await dialog.getByRole('combobox', { name: 'Severity' }).selectOption('High')
  await dialog.getByRole('combobox', { name: 'Category' }).selectOption('Cybercrime')

  const responsePromise = page.waitForResponse((response) => {
    const url = new URL(response.url())
    return response.request().method() === 'POST' && url.pathname === '/api/cases'
  })
  await dialog.getByRole('button', { name: 'Create case' }).click()
  const response = await responsePromise
  expect(response.status()).toBe(201)

  const payload = await response.json() as Record<string, unknown>
  const caseId = typeof payload.id === 'string' ? payload.id : ''
  expect(caseId).toMatch(/^[0-9a-f-]{36}$/i)

  const caseDialog = page.getByRole('dialog', { name: title })
  await expect(caseDialog).toBeVisible()
  await expect(page.getByRole('status')).toContainText('was created')

  return { caseId, caseDialog }
}

test.describe.serial('case workflows', () => {
  test('an analyst can sign in, create a case, and upload hashed evidence', async ({ page }) => {
    await signIn(page)
    const title = `Evidence workflow ${randomUUID().slice(0, 8)}`
    const { caseDialog } = await createCase(page, title)

    await caseDialog.getByRole('tab', { name: /^Evidence/ }).click()
    const contents = Buffer.from('CaseLedger browser evidence fixture\n', 'utf8')
    const fileName = `evidence-${randomUUID().slice(0, 8)}.txt`
    const expectedHash = createHash('sha256').update(contents).digest('hex')
    const evidenceResponse = page.waitForResponse((response) => {
      const url = new URL(response.url())
      return response.request().method() === 'POST' && url.pathname.endsWith('/evidence')
    })

    await caseDialog.locator('input[type="file"]').setInputFiles({
      name: fileName,
      mimeType: 'text/plain',
      buffer: contents,
    })

    expect((await evidenceResponse).status()).toBe(201)
    await expect(caseDialog.getByText(fileName, { exact: true })).toBeVisible()
    await expect(caseDialog.getByText(expectedHash, { exact: true })).toBeVisible()
    await expect(page.getByRole('status')).toContainText('securely uploaded and hashed')
  })

  test('verification detects an audit row changed directly in PostgreSQL', async ({ page }) => {
    await signIn(page)
    const title = `Tamper detection ${randomUUID().slice(0, 8)}`
    const { caseId, caseDialog } = await createCase(page, title)

    await caseDialog.getByRole('button', { name: 'Verify now' }).click()
    await expect(caseDialog.getByText(/Verified · \d+ events checked/)).toBeVisible()

    tamperWithFirstAuditEvent(caseId)

    await caseDialog.getByRole('button', { name: 'Verify now' }).click()
    await expect(caseDialog.getByText('Integrity break detected at event 1')).toBeVisible()
    await expect(
      page.getByRole('status').filter({
        hasText: 'A break was detected in the activity chain.',
      }),
    ).toBeVisible()
  })

  test('duplicate request and result deliveries remain idempotent', async ({ page }) => {
    await signIn(page)
    const title = `Idempotent verification ${randomUUID().slice(0, 8)}`
    const { caseId, caseDialog } = await createCase(page, title)
    await clearWebhookDeliveries()
    const queuedResponse = page.waitForResponse((response) => {
      const url = new URL(response.url())
      return response.request().method() === 'POST' &&
        url.pathname === `/api/cases/${caseId}/audit/verifications`
    })

    await caseDialog.getByRole('button', { name: 'Verify now' }).click()
    const response = await queuedResponse
    expect(response.status()).toBe(202)
    const queued = await response.json() as { id?: unknown }
    const jobId = typeof queued.id === 'string' ? queued.id : ''
    expect(jobId).toMatch(/^[0-9a-f-]{36}$/i)
    await expect(caseDialog.getByText(/Verified · \d+ events checked/)).toBeVisible()
    await expect.poll(async () => (await webhookDeliveries()).length).toBe(1)
    await expect.poll(() => workerInboxCount(jobId)).toBe(1)
    await expect.poll(() => apiOutboxAttemptCount(jobId)).toBeGreaterThanOrEqual(1)
    await expect.poll(() => workerResultPublishAttempts(jobId)).toBeGreaterThanOrEqual(1)

    replayApiVerificationRequest(jobId)
    await expect.poll(() => apiOutboxAttemptCount(jobId)).toBeGreaterThanOrEqual(2)
    await waitForVerificationQueuesToDrain()
    expect(workerInboxCount(jobId)).toBe(1)

    const originalResultAttempts = workerResultPublishAttempts(jobId)
    replayWorkerVerificationResult(jobId)
    await expect.poll(() => workerResultPublishAttempts(jobId))
      .toBeGreaterThanOrEqual(originalResultAttempts + 1)
    await waitForVerificationQueuesToDrain()

    const stored = await page.evaluate(async ({ caseId, jobId }) => {
      const result = await fetch(`/api/cases/${caseId}/audit/verifications/${jobId}`)
      return { status: result.status, body: await result.json() }
    }, { caseId, jobId })
    expect(stored.status).toBe(200)
    expect(stored.body).toMatchObject({
      id: jobId,
      status: 'Completed',
      valid: true,
      resultId: `audit-verification:${jobId}:v1`,
    })
    const deliveries = await webhookDeliveries()
    expect(deliveries).toHaveLength(1)
    expect(deliveries[0]).toMatchObject({
      eventType: 'caseledger.audit.verification.completed.v1',
    })
    expect(deliveries[0]?.deliveryId).toMatch(/^[0-9a-f-]{36}$/i)
    expect(deliveries[0]?.bodySha256).toMatch(/^[0-9a-f]{64}$/)
  })
})
