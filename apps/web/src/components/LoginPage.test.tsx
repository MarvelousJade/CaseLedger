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

describe('LoginPage', () => {
  it('submits the entered credentials and returns the authenticated user', async () => {
    const user = userEvent.setup()
    const onAuthenticated = vi.fn()
    const login = vi.spyOn(api, 'login').mockResolvedValue(analyst)

    render(<LoginPage onAuthenticated={onAuthenticated} />)

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

    render(<LoginPage onAuthenticated={onAuthenticated} />)
    await user.click(screen.getByRole('button', { name: 'Sign in securely' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Credentials rejected')
    expect(screen.getByRole('button', { name: 'Sign in securely' })).toBeEnabled()
    expect(onAuthenticated).not.toHaveBeenCalled()
  })

  it('exposes an accessible password visibility control', async () => {
    const user = userEvent.setup()

    render(<LoginPage onAuthenticated={vi.fn()} />)

    const password = screen.getByLabelText('Password')
    expect(password).toHaveAttribute('type', 'password')

    await user.click(screen.getByRole('button', { name: 'Show password' }))
    expect(password).toHaveAttribute('type', 'text')
    expect(screen.getByRole('button', { name: 'Hide password' })).toBeVisible()

    await user.click(screen.getByRole('button', { name: 'Hide password' }))
    expect(password).toHaveAttribute('type', 'password')
  })
})
