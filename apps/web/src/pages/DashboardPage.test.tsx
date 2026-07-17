import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import type { CaseItem, DashboardData, User } from '../types'
import { DashboardPage } from './DashboardPage'

const user: User = {
  id: 'user-1',
  name: 'Avery Singh',
  email: 'avery@example.com',
  role: 'Analyst',
}

const activeCase: CaseItem = {
  id: 'case-1',
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

const resolvedCase: CaseItem = {
  ...activeCase,
  id: 'case-2',
  reference: 'CL-2026-0041',
  title: 'Validate mobile evidence package',
  status: 'Resolved',
  severity: 'Medium',
}

const dashboard: DashboardData = {
  totalCases: 8,
  openCases: 5,
  criticalCases: 2,
  resolvedCases: 3,
  integrityStatus: 'Verified',
  recentCases: [activeCase],
}

function renderDashboard(overrides: Partial<Parameters<typeof DashboardPage>[0]> = {}) {
  const props = {
    user,
    refreshKey: 0,
    onCreate: vi.fn(),
    onSelectCase: vi.fn(),
    onViewCases: vi.fn(),
    ...overrides,
  }
  render(<DashboardPage {...props} />)
  return props
}

describe('DashboardPage', () => {
  it('falls back to a current case rollup when live metrics are unavailable', async () => {
    const browser = userEvent.setup()
    vi.spyOn(api, 'getDashboard').mockRejectedValue(new Error('Metrics unavailable'))
    vi.spyOn(api, 'getCases').mockResolvedValue({
      items: [activeCase, resolvedCase],
      total: 2,
      page: 1,
      pageSize: 50,
      totalPages: 1,
      hasNextPage: false,
      hasPreviousPage: false,
    })
    const { onSelectCase } = renderDashboard()

    expect(await screen.findByText(/Live dashboard metrics are unavailable/)).toBeVisible()
    expect(screen.getByRole('heading', { name: 'Verification pending' })).toBeVisible()

    const statistics = screen.getByRole('region', { name: 'Case statistics' })
    const totalCard = within(statistics).getByRole('heading', { name: 'Total cases' }).closest('article')
    const criticalCard = within(statistics).getByRole('heading', { name: 'Critical priority' }).closest('article')
    expect(totalCard).not.toBeNull()
    expect(criticalCard).not.toBeNull()
    expect(within(totalCard!).getByText('2')).toBeVisible()
    expect(within(criticalCard!).getByText('1')).toBeVisible()

    await browser.click(screen.getByRole('button', { name: /Review access-control policy anomaly/ }))
    expect(onSelectCase).toHaveBeenCalledWith(activeCase.id)
  })

  it('shows an error state and recovers when the user retries', async () => {
    const browser = userEvent.setup()
    vi.spyOn(api, 'getDashboard')
      .mockRejectedValueOnce(new Error('Dashboard unavailable'))
      .mockResolvedValueOnce(dashboard)
    vi.spyOn(api, 'getCases')
      .mockRejectedValueOnce(new Error('Cases unavailable'))
      .mockResolvedValueOnce({
        items: [],
        total: 0,
        page: 1,
        pageSize: 50,
        totalPages: 0,
        hasNextPage: false,
        hasPreviousPage: false,
      })
    renderDashboard()

    expect(await screen.findByRole('heading', { name: 'We hit a snag' })).toBeVisible()
    expect(screen.getByText('Dashboard unavailable')).toBeVisible()

    await browser.click(screen.getByRole('button', { name: /Try again/ }))

    await waitFor(() => {
      expect(screen.getByRole('region', { name: 'Case statistics' })).toBeVisible()
      expect(screen.getByRole('heading', { name: 'Verified' })).toBeVisible()
    })
    expect(screen.queryByRole('heading', { name: 'We hit a snag' })).not.toBeInTheDocument()
  })
})
