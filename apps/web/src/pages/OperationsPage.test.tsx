import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import type { OperationalFailure, OperationalFailureCollection } from '../types'
import { OperationsPage } from './OperationsPage'

const deadLetteredAt = '2026-07-18T03:00:00.123456Z'

function makeFailure(overrides: Partial<OperationalFailure> = {}): OperationalFailure {
  return {
    kind: 'verification-request',
    id: '11111111-1111-1111-1111-111111111111',
    caseId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    caseReference: 'CL-2026-0042',
    verificationJobId: '11111111-1111-1111-1111-111111111111',
    occurredAt: '2026-07-18T02:59:00Z',
    deadLetteredAt,
    attemptCount: 8,
    errorCode: 'BROKER_UNAVAILABLE',
    replayable: true,
    ...overrides,
  }
}

function makeCollection(items: OperationalFailure[]): OperationalFailureCollection {
  return {
    items,
    total: items.length,
    page: 1,
    pageSize: 25,
    totalPages: items.length ? 1 : 0,
  }
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('OperationsPage', () => {
  it('lists redacted failure details and clearly marks terminal verification jobs immutable', async () => {
    const terminal = makeFailure({
      kind: 'verification-job',
      id: '22222222-2222-2222-2222-222222222222',
      caseReference: 'CL-2026-0043',
      verificationJobId: '22222222-2222-2222-2222-222222222222',
      deadLetteredAt: '2026-07-18T03:05:00Z',
      attemptCount: undefined,
      errorCode: 'RETRY_EXHAUSTED',
      replayable: false,
      replayBlockedReason: 'Terminal verification jobs are immutable; queue a new verification instead.',
    })
    vi.spyOn(api, 'getOperationalFailures').mockResolvedValue(makeCollection([
      makeFailure(),
      terminal,
    ]))

    render(<OperationsPage />)

    expect(await screen.findByText('CL-2026-0042')).toBeVisible()
    expect(screen.getByText('BROKER_UNAVAILABLE')).toBeVisible()
    expect(screen.getByText('8')).toBeVisible()
    const terminalHeading = screen.getByText('Verification Job')
    const terminalRow = terminalHeading.closest('article')
    expect(terminalRow).not.toBeNull()
    expect(within(terminalRow!).getByText('Terminal · non-replayable')).toBeVisible()
    expect(within(terminalRow!).getByText(/Terminal verification jobs are immutable/)).toBeVisible()
    expect(within(terminalRow!).queryByRole('button', { name: 'Replay' })).not.toBeInTheDocument()
  })

  it('requires a short reason, submits the observed timestamp, and refreshes after request replay', async () => {
    const browser = userEvent.setup()
    const failure = makeFailure()
    const getFailures = vi.spyOn(api, 'getOperationalFailures')
      .mockResolvedValueOnce(makeCollection([failure]))
      .mockResolvedValueOnce(makeCollection([]))
    const replay = vi.spyOn(api, 'replayVerificationRequest').mockResolvedValue({
      replayId: '33333333-3333-3333-3333-333333333333',
      kind: 'verification-request',
      sourceId: failure.id,
      sourceDeadLetteredAt: deadLetteredAt,
      replayedAt: '2026-07-18T03:10:00Z',
    })

    render(<OperationsPage />)
    await browser.click(await screen.findByRole('button', { name: 'Replay' }))

    expect(screen.getByText(deadLetteredAt)).toBeVisible()
    const confirm = screen.getByRole('button', { name: 'Confirm replay' })
    expect(confirm).toBeDisabled()
    await browser.type(screen.getByLabelText(/Recovery reason/), 'No')
    expect(confirm).toBeDisabled()
    await browser.type(screen.getByLabelText(/Recovery reason/), 'w repaired')
    expect(confirm).toBeEnabled()
    await browser.click(confirm)

    await waitFor(() => expect(replay).toHaveBeenCalledWith(failure.id, {
      deadLetteredAt,
      reason: 'Now repaired',
    }))
    expect(await screen.findByRole('status')).toHaveTextContent('Verification Request replay accepted')
    await waitFor(() => expect(getFailures).toHaveBeenCalledTimes(2))
    expect(screen.getByText('No operational failures')).toBeVisible()
  })

  it('routes eligible webhook recovery through the webhook replay endpoint', async () => {
    const browser = userEvent.setup()
    const failure = makeFailure({
      kind: 'webhook-delivery',
      id: '44444444-4444-4444-4444-444444444444',
      errorCode: 'WEBHOOK_HTTP_503',
    })
    vi.spyOn(api, 'getOperationalFailures')
      .mockResolvedValueOnce(makeCollection([failure]))
      .mockResolvedValueOnce(makeCollection([]))
    const replay = vi.spyOn(api, 'replayWebhook').mockResolvedValue({
      replayId: '55555555-5555-5555-5555-555555555555',
      kind: 'webhook-delivery',
      sourceId: failure.id,
      sourceDeadLetteredAt: deadLetteredAt,
      replayedAt: '2026-07-18T03:11:00Z',
    })

    render(<OperationsPage />)
    await browser.click(await screen.findByRole('button', { name: 'Replay' }))
    await browser.type(screen.getByLabelText(/Recovery reason/), 'Receiver deployment repaired')
    await browser.click(screen.getByRole('button', { name: 'Confirm replay' }))

    await waitFor(() => expect(replay).toHaveBeenCalledWith(failure.id, {
      deadLetteredAt,
      reason: 'Receiver deployment repaired',
    }))
  })
})
