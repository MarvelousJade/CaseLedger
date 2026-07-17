import { createHash, randomUUID } from 'node:crypto'
import { expect, test, type Page } from '@playwright/test'
import { tamperWithFirstAuditEvent } from './support/database'

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
  test('an analyst can sign in, create a case, and register an evidence fingerprint', async ({ page }) => {
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
    await expect(page.getByRole('status')).toContainText('fingerprinted and registered')
  })

  test('verification detects an audit row changed directly in PostgreSQL', async ({ page }) => {
    await signIn(page)
    const title = `Tamper detection ${randomUUID().slice(0, 8)}`
    const { caseId, caseDialog } = await createCase(page, title)

    await caseDialog.getByRole('button', { name: 'Verify now' }).click()
    await expect(caseDialog.getByText(/Verified · \d+ events checked/)).toBeVisible()

    tamperWithFirstAuditEvent(caseId)

    await caseDialog.getByRole('button', { name: 'Verify now' }).click()
    await expect(caseDialog.getByText('Integrity break detected at 1')).toBeVisible()
    await expect(page.getByRole('status')).toContainText('A break was detected in the activity chain.')
  })
})
