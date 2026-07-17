import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import { subscribeToCaseUpdates } from '../realtime'
import type { AuditVerificationJob, CaseItem, VerificationUpdated } from '../types'
import { CaseDetailDrawer } from './CaseDetailDrawer'

vi.mock('../realtime', () => ({
  subscribeToCaseUpdates: vi.fn(),
}))

const currentCase: CaseItem = {
  id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  version: '11111111-1111-1111-1111-111111111111',
  reference: 'CL-2026-0042',
  title: 'Review access-control policy anomaly',
  summary: 'Review an unexpected permission grant.',
  status: 'New',
  severity: 'Critical',
  category: 'Access Control',
  createdByName: 'Morgan Chen',
  createdAt: '2026-07-15T12:00:00.000Z',
  updatedAt: '2026-07-16T12:00:00.000Z',
  tags: [],
  evidence: [],
  activity: [],
}

const queuedJob: AuditVerificationJob = {
  id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
  status: 'Queued',
  targetSequence: 4,
  targetHash: 'a'.repeat(64),
  snapshotSha256: 'b'.repeat(64),
  requestedAt: '2026-07-17T02:00:00.000Z',
  isCurrent: true,
}

let realtimeHandler: ((update: VerificationUpdated) => void) | undefined

beforeEach(() => {
  realtimeHandler = undefined
  vi.mocked(subscribeToCaseUpdates).mockImplementation(async (_caseId, handler) => {
    realtimeHandler = handler
    return async () => undefined
  })
  vi.spyOn(api, 'getLatestAuditVerification').mockRejectedValue(new Error('No verification yet'))
})

function renderDrawer() {
  const props = { caseId: currentCase.id, onClose: vi.fn(), onMutate: vi.fn(), notify: vi.fn() }
  render(<CaseDetailDrawer {...props} />)
  return props
}

describe('CaseDetailDrawer status updates', () => {
  it('uses the loaded version and reloads the latest case after a stale update', async () => {
    const browser = userEvent.setup()
    const latestCase = {
      ...currentCase,
      version: '22222222-2222-2222-2222-222222222222',
      status: 'InProgress',
    }
    const getCase = vi.spyOn(api, 'getCase')
      .mockResolvedValueOnce(currentCase)
      .mockResolvedValueOnce(latestCase)
    const staleError = Object.assign(new Error('Case version is stale'), { status: 412 })
    const updateCase = vi.spyOn(api, 'updateCase').mockRejectedValue(staleError)
    const { notify, onMutate } = renderDrawer()

    const status = await screen.findByLabelText('Case status')
    await browser.selectOptions(status, 'Resolved')
    await browser.click(screen.getByRole('button', { name: 'Update' }))

    await waitFor(() => expect(getCase).toHaveBeenCalledTimes(2))
    expect(updateCase).toHaveBeenCalledOnce()
    expect(updateCase).toHaveBeenCalledWith(currentCase.id, currentCase.version, { status: 'Resolved' })
    expect(notify).toHaveBeenCalledWith(
      'This case changed since you opened it. The latest details have been reloaded.',
      'danger',
    )
    expect(onMutate).toHaveBeenCalledOnce()
    await waitFor(() => expect(status).toHaveValue('InProgress'))
  })

  it('reports an ordinary update error without reloading the case', async () => {
    const browser = userEvent.setup()
    const getCase = vi.spyOn(api, 'getCase').mockResolvedValue(currentCase)
    vi.spyOn(api, 'updateCase').mockRejectedValue(new Error('Connection lost'))
    const { notify, onMutate } = renderDrawer()

    await browser.selectOptions(await screen.findByLabelText('Case status'), 'Resolved')
    await browser.click(screen.getByRole('button', { name: 'Update' }))

    await waitFor(() => expect(notify).toHaveBeenCalledWith('Connection lost', 'danger'))
    expect(getCase).toHaveBeenCalledOnce()
    expect(onMutate).not.toHaveBeenCalled()
  })
})

describe('CaseDetailDrawer evidence uploads', () => {
  it('sends the selected file to the server without hashing it in the browser', async () => {
    const browser = userEvent.setup()
    const getCase = vi.spyOn(api, 'getCase').mockResolvedValue(currentCase)
    const addEvidence = vi.spyOn(api, 'addEvidence').mockResolvedValue({
      id: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
      fileName: 'server-hash.txt',
      sizeBytes: 14,
      mediaType: 'text/plain',
      sha256: 'a'.repeat(64),
      addedByName: 'Avery Singh',
      createdAt: '2026-07-17T12:00:00.000Z',
    })
    const digest = vi.spyOn(crypto.subtle, 'digest')
    const { notify, onMutate } = renderDrawer()

    await browser.click(await screen.findByRole('tab', { name: /^Evidence/ }))
    const file = new File(['evidence bytes'], 'server-hash.txt', { type: 'text/plain' })
    await browser.upload(screen.getByLabelText(/Upload evidence/), file)

    await waitFor(() => expect(addEvidence).toHaveBeenCalledWith(currentCase.id, file))
    expect(digest).not.toHaveBeenCalled()
    expect(notify).toHaveBeenCalledWith('server-hash.txt was securely uploaded and hashed.')
    expect(onMutate).toHaveBeenCalledOnce()
    await waitFor(() => expect(getCase).toHaveBeenCalledTimes(2))
  })
})

describe('CaseDetailDrawer audit verification', () => {
  it('queues a verification and applies its realtime result', async () => {
    const browser = userEvent.setup()
    const completedJob: AuditVerificationJob = {
      ...queuedJob,
      status: 'Completed',
      resultId: `audit-verification:${queuedJob.id}:v1`,
      valid: true,
      checkedEvents: 4,
      chainHead: queuedJob.targetHash,
      completedAt: '2026-07-17T02:00:01.000Z',
    }
    vi.spyOn(api, 'getCase').mockResolvedValue(currentCase)
    vi.spyOn(api, 'queueAuditVerification').mockResolvedValue(queuedJob)
    const getVerification = vi.spyOn(api, 'getAuditVerification').mockResolvedValue(completedJob)
    const { notify } = renderDrawer()

    await browser.click(await screen.findByRole('button', { name: 'Verify now' }))

    expect(await screen.findByText('Verification queued · 4 events in snapshot')).toBeVisible()
    expect(api.queueAuditVerification).toHaveBeenCalledWith(currentCase.id)
    expect(subscribeToCaseUpdates).toHaveBeenCalledWith(currentCase.id, expect.any(Function))

    act(() => {
      realtimeHandler?.({
        jobId: queuedJob.id,
        caseId: currentCase.id,
        resultId: completedJob.resultId!,
        status: 'Completed',
        valid: true,
        checkedEvents: 4,
        completedAt: completedJob.completedAt,
      })
    })

    expect(await screen.findByText('Verified · 4 events checked')).toBeVisible()
    expect(getVerification).toHaveBeenCalledWith(currentCase.id, queuedJob.id)
    expect(notify).toHaveBeenCalledWith('Integrity verified across 4 events.', 'success')
  })

  it('shows when the latest successful verification is stale', async () => {
    vi.spyOn(api, 'getCase').mockResolvedValue(currentCase)
    vi.mocked(api.getLatestAuditVerification).mockResolvedValue({
      ...queuedJob,
      status: 'Completed',
      valid: true,
      checkedEvents: 4,
      completedAt: '2026-07-17T02:00:01.000Z',
      isCurrent: false,
    })

    renderDrawer()

    expect(await screen.findByText('Verified snapshot is outdated · Run again for the current chain')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Verify again' })).toBeEnabled()
  })

  it('falls back to synchronous verification when messaging is unavailable', async () => {
    const browser = userEvent.setup()
    vi.spyOn(api, 'getCase').mockResolvedValue(currentCase)
    vi.spyOn(api, 'queueAuditVerification').mockRejectedValue(
      Object.assign(new Error('Verification messaging is unavailable'), { status: 503 }),
    )
    const verifyAudit = vi.spyOn(api, 'verifyAudit').mockResolvedValue({ valid: true, checkedEvents: 4 })
    const { notify } = renderDrawer()

    await browser.click(await screen.findByRole('button', { name: 'Verify now' }))

    expect(await screen.findByText('Verified · 4 events checked')).toBeVisible()
    expect(verifyAudit).toHaveBeenCalledWith(currentCase.id)
    expect(notify).toHaveBeenCalledWith('Integrity verified across 4 events.', 'success')
  })

  it('clears the previous verification when switching to a case with no result', async () => {
    const secondCase = {
      ...currentCase,
      id: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
      reference: 'CL-2026-0043',
      title: 'Second investigation',
    }
    vi.spyOn(api, 'getCase')
      .mockResolvedValueOnce(currentCase)
      .mockResolvedValueOnce(secondCase)
    vi.mocked(api.getLatestAuditVerification)
      .mockResolvedValueOnce({
        ...queuedJob,
        status: 'Completed',
        valid: true,
        checkedEvents: 4,
        completedAt: '2026-07-17T02:00:01.000Z',
      })
      .mockRejectedValueOnce(Object.assign(new Error('Verification job not found'), { status: 404 }))
    const props = { onClose: vi.fn(), onMutate: vi.fn(), notify: vi.fn() }
    const { rerender } = render(<CaseDetailDrawer caseId={currentCase.id} {...props} />)

    expect(await screen.findByText('Verified · 4 events checked')).toBeVisible()

    rerender(<CaseDetailDrawer caseId={secondCase.id} {...props} />)

    expect(await screen.findByRole('heading', { name: secondCase.title })).toBeVisible()
    await waitFor(() => {
      expect(screen.queryByText('Verified · 4 events checked')).not.toBeInTheDocument()
      expect(screen.queryByText(/Verification queued/)).not.toBeInTheDocument()
    })
  })
})
