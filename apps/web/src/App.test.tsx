import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import { api } from './api'

afterEach(() => {
  vi.restoreAllMocks()
})

describe('App authentication discovery', () => {
  it('fails closed when authentication capabilities cannot be loaded', async () => {
    vi.spyOn(api, 'me').mockRejectedValue(new Error('No active session'))
    vi.spyOn(api, 'authCapabilities').mockRejectedValue(new Error('Capabilities unavailable'))

    render(<App />)

    expect(await screen.findByRole('status')).toHaveTextContent('Sign-in is not available')
    expect(screen.queryByRole('button', { name: 'Sign in securely' })).not.toBeInTheDocument()
    expect(screen.queryByText('analyst@caseledger.dev')).not.toBeInTheDocument()
  })

  it('shows the operations navigation and page to Admin users', async () => {
    const browser = userEvent.setup()
    vi.spyOn(api, 'me').mockResolvedValue({
      id: 'admin-1',
      name: 'Morgan Chen',
      email: 'admin@caseledger.dev',
      role: 'Admin',
    })
    vi.spyOn(api, 'authCapabilities').mockResolvedValue({
      demoLoginEnabled: true,
      entraEnabled: false,
      showDemoCredentials: true,
    })
    vi.spyOn(api, 'getDashboard').mockResolvedValue({
      totalCases: 0,
      openCases: 0,
      criticalCases: 0,
      resolvedCases: 0,
      integrityStatus: 'Verified',
      recentCases: [],
    })
    vi.spyOn(api, 'getCases').mockResolvedValue({
      items: [], total: 0, page: 1, pageSize: 50, totalPages: 0,
      hasNextPage: false, hasPreviousPage: false,
    })
    const failures = vi.spyOn(api, 'getOperationalFailures').mockResolvedValue({
      items: [], total: 0, page: 1, pageSize: 25, totalPages: 0,
    })

    render(<App />)

    const operationsNav = await screen.findByRole('button', { name: 'Operations' })
    await browser.click(operationsNav)

    expect(await screen.findByRole('heading', { name: 'Operations' })).toBeVisible()
    expect(failures).toHaveBeenCalledWith({ kind: '', page: 1, pageSize: 25 })
  })

  it('does not expose operations navigation to non-Admin users', async () => {
    vi.spyOn(api, 'me').mockResolvedValue({
      id: 'analyst-1',
      name: 'Avery Singh',
      email: 'analyst@caseledger.dev',
      role: 'Analyst',
    })
    vi.spyOn(api, 'authCapabilities').mockResolvedValue({
      demoLoginEnabled: true,
      entraEnabled: false,
      showDemoCredentials: true,
    })
    vi.spyOn(api, 'getDashboard').mockResolvedValue({
      totalCases: 0,
      openCases: 0,
      criticalCases: 0,
      resolvedCases: 0,
      integrityStatus: 'Verified',
      recentCases: [],
    })
    vi.spyOn(api, 'getCases').mockResolvedValue({
      items: [], total: 0, page: 1, pageSize: 50, totalPages: 0,
      hasNextPage: false, hasPreviousPage: false,
    })

    render(<App />)

    expect(await screen.findByRole('button', { name: 'Overview' })).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Operations' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Operations' })).not.toBeInTheDocument()
  })
})
