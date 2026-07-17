import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import type { User } from '../types'
import { LoginPage } from './LoginPage'

const analyst: User = {
  id: 'user-1',
  name: 'Avery Singh',
  email: 'avery@example.com',
  role: 'Analyst',
}

const demoCapabilities = {
  demoLoginEnabled: true,
  entraEnabled: false,
  showDemoCredentials: true,
}

describe('LoginPage', () => {
  it('submits the entered credentials and returns the authenticated user', async () => {
    const user = userEvent.setup()
    const onAuthenticated = vi.fn()
    const login = vi.spyOn(api, 'login').mockResolvedValue(analyst)

    render(<LoginPage capabilities={demoCapabilities} onAuthenticated={onAuthenticated} />)

    const email = screen.getByRole('textbox', { name: 'Email address' })
    const password = screen.getByLabelText('Password')
    await user.clear(email)
    await user.type(email, analyst.email)
    await user.clear(password)
    await user.type(password, 'correct horse battery staple')
    await user.click(screen.getByRole('button', { name: 'Sign in securely' }))

    await waitFor(() => {
      expect(login).toHaveBeenCalledWith(analyst.email, 'correct horse battery staple')
      expect(onAuthenticated).toHaveBeenCalledWith(analyst)
    })
  })

  it('announces a failed sign-in and allows another attempt', async () => {
    const user = userEvent.setup()
    const onAuthenticated = vi.fn()
    vi.spyOn(api, 'login').mockRejectedValue(new Error('Credentials rejected'))

    render(<LoginPage capabilities={demoCapabilities} onAuthenticated={onAuthenticated} />)
    await user.click(screen.getByRole('button', { name: 'Sign in securely' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Credentials rejected')
    expect(screen.getByRole('button', { name: 'Sign in securely' })).toBeEnabled()
    expect(onAuthenticated).not.toHaveBeenCalled()
  })

  it('exposes an accessible password visibility control', async () => {
    const user = userEvent.setup()

    render(<LoginPage capabilities={demoCapabilities} onAuthenticated={vi.fn()} />)

    const password = screen.getByLabelText('Password')
    expect(password).toHaveAttribute('type', 'password')

    await user.click(screen.getByRole('button', { name: 'Show password' }))
    expect(password).toHaveAttribute('type', 'text')
    expect(screen.getByRole('button', { name: 'Hide password' })).toBeVisible()

    await user.click(screen.getByRole('button', { name: 'Hide password' }))
    expect(password).toHaveAttribute('type', 'password')
  })

  it('offers Microsoft sign-in without exposing demo credentials when only Entra is enabled', () => {
    render(
      <LoginPage
        capabilities={{ demoLoginEnabled: false, entraEnabled: true, showDemoCredentials: false }}
        onAuthenticated={vi.fn()}
      />,
    )

    expect(screen.getByRole('link', { name: 'Continue with Microsoft' })).toHaveAttribute(
      'href',
      '/api/auth/entra/login?returnUrl=/',
    )
    expect(screen.queryByRole('textbox', { name: 'Email address' })).not.toBeInTheDocument()
    expect(screen.queryByText('analyst@caseledger.dev')).not.toBeInTheDocument()
  })

  it('keeps both configured sign-in methods available', () => {
    render(
      <LoginPage
        capabilities={{ demoLoginEnabled: true, entraEnabled: true, showDemoCredentials: true }}
        onAuthenticated={vi.fn()}
      />,
    )

    expect(screen.getByRole('link', { name: 'Continue with Microsoft' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Sign in securely' })).toBeVisible()
    expect(screen.getByText('Or use local sign-in')).toBeVisible()
  })

  it('supports private local credentials without publishing demo values', () => {
    render(
      <LoginPage
        capabilities={{
          demoLoginEnabled: true,
          entraEnabled: false,
          showDemoCredentials: false,
        }}
        onAuthenticated={vi.fn()}
      />,
    )

    expect(screen.getByRole('textbox', { name: 'Email address' })).toHaveValue('')
    expect(screen.getByLabelText('Password')).toHaveValue('')
    expect(screen.queryByText('analyst@caseledger.dev')).not.toBeInTheDocument()
  })

  it('explains when no authentication method is configured', () => {
    render(
      <LoginPage
        capabilities={{ demoLoginEnabled: false, entraEnabled: false, showDemoCredentials: false }}
        onAuthenticated={vi.fn()}
      />,
    )

    expect(screen.getByRole('status')).toHaveTextContent('Sign-in is not available')
    expect(screen.getByRole('status')).toHaveTextContent('Contact an administrator')
    expect(screen.queryByRole('button', { name: 'Sign in securely' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Continue with Microsoft' })).not.toBeInTheDocument()
  })

  it('shows a fixed local message after an external sign-in failure', () => {
    window.history.replaceState({}, '', '/?authError=external-sign-in-failed')
    try {
      render(
        <LoginPage
          capabilities={{
            demoLoginEnabled: false,
            entraEnabled: true,
            showDemoCredentials: false,
          }}
          onAuthenticated={vi.fn()}
        />,
      )

      expect(screen.getByRole('alert')).toHaveTextContent(
        'Microsoft sign-in could not be completed',
      )
    } finally {
      window.history.replaceState({}, '', '/')
    }
  })
})
