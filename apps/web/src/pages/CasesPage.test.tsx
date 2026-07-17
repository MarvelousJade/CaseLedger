import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import type { CaseCollection, CaseItem } from '../types'
import { CasesPage } from './CasesPage'

const pageSize = 10

function makeCase(index: number, overrides: Partial<CaseItem> = {}): CaseItem {
  return {
    id: `case-${index}`,
    version: '11111111-1111-1111-1111-111111111111',
    reference: `CL-2026-${String(index).padStart(4, '0')}`,
    title: `Case ${index}`,
    summary: `Summary for case ${index}`,
    status: 'New',
    severity: 'Medium',
    category: 'Compliance',
    createdByName: 'Avery Singh',
    createdAt: '2026-07-15T12:00:00.000Z',
    updatedAt: '2026-07-16T12:00:00.000Z',
    tags: [],
    evidence: [],
    activity: [],
    ...overrides,
  }
}

function makeCollection(page: number, total = 25): CaseCollection {
  const first = (page - 1) * pageSize + 1
  const count = Math.min(pageSize, total - first + 1)
  const items = Array.from({ length: Math.max(0, count) }, (_, index) => makeCase(first + index))
  const totalPages = total ? Math.ceil(total / pageSize) : 0
  return {
    items,
    total,
    page,
    pageSize,
    totalPages,
    hasNextPage: page < totalPages,
    hasPreviousPage: page > 1,
  }
}

function renderCasesPage() {
  const props = { refreshKey: 0, onCreate: vi.fn(), onSelectCase: vi.fn() }
  render(<CasesPage {...props} />)
  return props
}

describe('CasesPage pagination', () => {
  it('moves between server pages and resets to page one when a filter changes', async () => {
    const browser = userEvent.setup()
    const filteredCase = makeCase(99, { title: 'Resolved filtered case', status: 'Resolved' })
    const getCases = vi.spyOn(api, 'getCases').mockImplementation(async (filters = {}) => {
      if (filters.status === 'Resolved') {
        return {
          items: [filteredCase],
          total: 1,
          page: 1,
          pageSize,
          totalPages: 1,
          hasNextPage: false,
          hasPreviousPage: false,
        }
      }
      return makeCollection(filters.page ?? 1)
    })
    renderCasesPage()

    expect(await screen.findAllByText('Case 1')).toHaveLength(2)
    expect(getCases).toHaveBeenLastCalledWith({ search: '', status: '', severity: '', page: 1, pageSize })
    const pagination = screen.getByRole('navigation', { name: 'Case pages' })
    expect(within(pagination).getByText('Showing 1–10 of 25 cases')).toBeVisible()
    expect(within(pagination).getByRole('button', { name: 'Previous' })).toBeDisabled()

    await browser.click(within(pagination).getByRole('button', { name: 'Next' }))

    expect(await screen.findAllByText('Case 11')).toHaveLength(2)
    expect(getCases).toHaveBeenLastCalledWith({ search: '', status: '', severity: '', page: 2, pageSize })
    expect(screen.getByText('Showing 11–20 of 25 cases')).toBeVisible()

    await browser.selectOptions(screen.getByLabelText('Filter by status'), 'Resolved')

    expect(await screen.findAllByText('Resolved filtered case')).toHaveLength(2)
    expect(getCases).toHaveBeenLastCalledWith({ search: '', status: 'Resolved', severity: '', page: 1, pageSize })
    expect(screen.queryByRole('navigation', { name: 'Case pages' })).not.toBeInTheDocument()

    await browser.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(await screen.findAllByText('Case 1')).toHaveLength(2)
    expect(screen.getByLabelText('Filter by status')).toHaveValue('')
    expect(getCases).toHaveBeenLastCalledWith({ search: '', status: '', severity: '', page: 1, pageSize })
  })

  it('keeps debounced search and clear-search behavior on the first page', async () => {
    const browser = userEvent.setup()
    const matchingCase = makeCase(42, { title: 'Matching anomaly case' })
    const getCases = vi.spyOn(api, 'getCases').mockImplementation(async (filters = {}) => {
      if (filters.search) {
        return {
          items: [matchingCase],
          total: 1,
          page: 1,
          pageSize,
          totalPages: 1,
          hasNextPage: false,
          hasPreviousPage: false,
        }
      }
      return makeCollection(1)
    })
    renderCasesPage()
    await screen.findAllByText('Case 1')

    await browser.type(screen.getByLabelText('Search cases'), 'anomaly')

    expect(await screen.findAllByText('Matching anomaly case')).toHaveLength(2)
    expect(getCases).toHaveBeenLastCalledWith({ search: 'anomaly', status: '', severity: '', page: 1, pageSize })
    const callsBeforeClear = getCases.mock.calls.length

    await browser.click(screen.getByRole('button', { name: 'Clear search' }))

    await waitFor(() => expect(getCases.mock.calls.length).toBeGreaterThan(callsBeforeClear))
    expect(await screen.findAllByText('Case 1')).toHaveLength(2)
    expect(screen.getByLabelText('Search cases')).toHaveValue('')
    expect(getCases).toHaveBeenLastCalledWith({ search: '', status: '', severity: '', page: 1, pageSize })
  })
})
