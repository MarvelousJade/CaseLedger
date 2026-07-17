import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import type { CaseItem } from '../types'
import { CaseDetailDrawer } from './CaseDetailDrawer'

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
