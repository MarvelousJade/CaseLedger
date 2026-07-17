import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import type { CaseItem, User } from '../types'
import { CreateCaseModal } from './CreateCaseModal'

const assignee: User = {
  id: 'user-2',
  name: 'Morgan Chen',
  email: 'morgan@example.com',
  role: 'Investigator',
}

const createdCase: CaseItem = {
  id: 'case-1',
  reference: 'CL-2026-0100',
  title: 'Credential exposure review',
  summary: 'Review the reported credential exposure.',
  status: 'New',
  severity: 'Critical',
  category: 'Compliance',
  assigneeId: assignee.id,
  assigneeName: assignee.name,
  createdByName: 'Avery Singh',
  createdAt: '2026-07-16T12:00:00.000Z',
  updatedAt: '2026-07-16T12:00:00.000Z',
  dueAt: '2026-08-01T12:00:00.000Z',
  tags: ['identity', 'external', 'priority'],
  evidence: [],
  activity: [],
}

function renderModal(overrides: Partial<Parameters<typeof CreateCaseModal>[0]> = {}) {
  const props = {
    onClose: vi.fn(),
    onCreated: vi.fn(),
    notify: vi.fn(),
    ...overrides,
  }
  const view = render(<CreateCaseModal {...props} />)
  return { ...view, ...props }
}

describe('CreateCaseModal', () => {
  it('identifies itself as a dialog, focuses the first field, and closes with Escape', async () => {
    const user = userEvent.setup()
    vi.spyOn(api, 'getUsers').mockResolvedValue([])
    const { onClose, unmount } = renderModal()

    expect(screen.getByRole('dialog', { name: 'Create a case' })).toBeVisible()
    await waitFor(() => expect(screen.getByLabelText(/Case title/)).toHaveFocus())
    expect(document.body).toHaveClass('modal-open')

    await user.keyboard('{Escape}')
    expect(onClose).toHaveBeenCalledOnce()

    unmount()
    expect(document.body).not.toHaveClass('modal-open')
  })

  it('keeps creation disabled until required fields are present and submits normalized data', async () => {
    const user = userEvent.setup()
    vi.spyOn(api, 'getUsers').mockResolvedValue([assignee])
    const createCase = vi.spyOn(api, 'createCase').mockResolvedValue(createdCase)
    const { onCreated, notify } = renderModal()
    const submit = screen.getByRole('button', { name: 'Create case' })

    expect(submit).toBeDisabled()
    await user.type(screen.getByLabelText(/Case title/), '  Credential exposure review  ')
    expect(submit).toBeDisabled()
    await user.type(screen.getByLabelText(/Summary/), '  Review the reported credential exposure.  ')
    expect(submit).toBeEnabled()

    await user.selectOptions(screen.getByLabelText('Severity'), 'Critical')
    await user.selectOptions(screen.getByLabelText('Category'), 'Compliance')
    await screen.findByRole('option', { name: assignee.name })
    await user.selectOptions(screen.getByLabelText('Assignee'), assignee.id)
    fireEvent.change(screen.getByLabelText('Target date'), { target: { value: '2026-08-01' } })
    await user.type(screen.getByLabelText(/Tags/), '#identity, external, , priority')
    await user.click(submit)

    await waitFor(() => expect(createCase).toHaveBeenCalledWith({
      title: 'Credential exposure review',
      summary: 'Review the reported credential exposure.',
      severity: 'Critical',
      category: 'Compliance',
      assigneeId: assignee.id,
      dueAt: new Date('2026-08-01T12:00:00').toISOString(),
      tags: ['identity', 'external', 'priority'],
    }))
    expect(onCreated).toHaveBeenCalledWith(createdCase)
    expect(notify).not.toHaveBeenCalled()
  })
})
