import { useState, type FormEvent } from 'react'
import { api, getErrorMessage } from '../api'
import { BrandMark, Icon } from '../Icons'
import type { User } from '../types'

export function LoginPage({ onAuthenticated }: { onAuthenticated: (user: User) => void }) {
  const [email, setEmail] = useState('analyst@caseledger.dev')
  const [password, setPassword] = useState('Analyst123!')
  const [showPassword, setShowPassword] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSubmitting(true)
    setError('')
    try {
      onAuthenticated(await api.login(email.trim(), password))
    } catch (requestError) {
      setError(getErrorMessage(requestError, 'We could not sign you in. Check your details and try again.'))
    } finally {
      setSubmitting(false)
    }
  }

  const fillCredentials = () => {
    setEmail('analyst@caseledger.dev')
    setPassword('Analyst123!')
    setError('')
  }

  return (
    <main className="login-page">
      <section className="login-story" aria-label="CaseLedger overview">
        <div className="login-story__glow" />
        <a className="brand brand--light" href="/" aria-label="CaseLedger home">
          <BrandMark /><span>CaseLedger</span>
        </a>

        <div className="login-message">
          <span className="eyebrow eyebrow--light"><Icon name="fingerprint" size={16} /> Evidence, accounted for</span>
          <h1>Every action leaves a trustworthy trail.</h1>
          <p>Investigate, collaborate, and preserve the integrity of every case from first report to resolution.</p>
        </div>

        <div className="login-preview" aria-hidden="true">
          <div className="preview-topline">
            <span className="preview-kicker">ACTIVE CASE</span>
            <span className="preview-integrity"><Icon name="shield" size={13} /> Verified</span>
          </div>
          <div className="preview-title-row">
            <span className="preview-icon"><Icon name="briefcase" size={20} /></span>
            <div><b>CL-2026-0142</b><span>Credential exposure review</span></div>
          </div>
          <div className="preview-track"><i /><i /><i /><i /></div>
          <div className="preview-foot"><span>12 events secured</span><span>Updated 8 min ago</span></div>
        </div>

        <p className="login-quote">“Trust is built into the record—not added after the fact.”</p>
      </section>

      <section className="login-panel">
        <div className="login-panel__mobile-brand">
          <a className="brand" href="/" aria-label="CaseLedger home"><BrandMark /><span>CaseLedger</span></a>
        </div>
        <div className="login-card">
          <div className="login-card__heading">
            <span className="eyebrow">SECURE WORKSPACE</span>
            <h2>Welcome back</h2>
            <p>Sign in to continue to your investigation workspace.</p>
          </div>

          {error && <div className="alert alert--danger" role="alert"><Icon name="warning" size={18} /><span>{error}</span></div>}

          <form onSubmit={submit} className="form-stack">
            <label className="field">
              <span>Email address</span>
              <div className="input-wrap"><Icon name="user" size={18} /><input type="email" autoComplete="username" value={email} onChange={(event) => setEmail(event.target.value)} required /></div>
            </label>
            <label className="field">
              <span>Password</span>
              <div className="input-wrap">
                <Icon name="hash" size={18} />
                <input type={showPassword ? 'text' : 'password'} autoComplete="current-password" value={password} onChange={(event) => setPassword(event.target.value)} required />
                <button className="input-action" type="button" onClick={() => setShowPassword((current) => !current)} aria-label={showPassword ? 'Hide password' : 'Show password'}>{showPassword ? 'Hide' : 'Show'}</button>
              </div>
            </label>
            <button className="button button--primary button--large" disabled={submitting} type="submit">
              {submitting ? <><span className="button-spinner" /> Signing in…</> : <>Sign in securely <Icon name="arrow-right" size={18} /></>}
            </button>
          </form>

          <div className="demo-access">
            <div className="section-divider"><span>Demo access</span></div>
            <div className="credential-grid credential-grid--single">
              <button type="button" className="credential-card" onClick={fillCredentials}>
                <span className="avatar avatar--green">AN</span><span><b>Analyst</b><small>analyst@caseledger.dev</small></span><Icon name="arrow-right" size={16} />
              </button>
            </div>
            <p className="credential-hint">Use the shared analyst account to explore the workspace.</p>
          </div>
        </div>
        <p className="login-footer"><Icon name="shield" size={14} /> Protected by secure, HTTP-only session cookies</p>
      </section>
    </main>
  )
}
