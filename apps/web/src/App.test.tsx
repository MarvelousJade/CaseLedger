import { render, screen } from '@testing-library/react'
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
})
