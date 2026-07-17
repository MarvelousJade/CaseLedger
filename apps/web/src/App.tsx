import { useEffect, useState } from 'react'
import { api } from './api'
import { BrandMark, Icon, type IconName } from './Icons'
import type { AuthCapabilities, User } from './types'
import { initials, titleCase } from './utils'
import { CaseDetailDrawer } from './components/CaseDetailDrawer'
import { CreateCaseModal } from './components/CreateCaseModal'
import { LoginPage } from './components/LoginPage'
import { CasesPage } from './pages/CasesPage'
import { DashboardPage } from './pages/DashboardPage'
import { IntegrityPage } from './pages/IntegrityPage'
import './App.css'

type View = 'dashboard' | 'cases' | 'integrity'

const unavailableAuthCapabilities: AuthCapabilities = {
  demoLoginEnabled: false,
  entraEnabled: false,
  showDemoCredentials: false,
}

function App() {
  const [session, setSession] = useState<'checking' | 'guest' | 'authenticated'>('checking')
  const [user, setUser] = useState<User | null>(null)
  const [authCapabilities, setAuthCapabilities] = useState<AuthCapabilities>(unavailableAuthCapabilities)

  useEffect(() => {
    let active = true
    Promise.allSettled([api.me(), api.authCapabilities()])
      .then(([sessionResult, capabilitiesResult]) => {
        if (!active) return
        setAuthCapabilities(
          capabilitiesResult.status === 'fulfilled'
            ? capabilitiesResult.value
            : unavailableAuthCapabilities,
        )
        if (sessionResult.status === 'fulfilled') {
          setUser(sessionResult.value)
          setSession('authenticated')
        } else {
          setSession('guest')
        }
      })
    return () => { active = false }
  }, [])

  if (session === 'checking') return <LaunchScreen />

  if (session === 'guest' || !user) {
    return <LoginPage capabilities={authCapabilities} onAuthenticated={(authenticatedUser) => { setUser(authenticatedUser); setSession('authenticated') }} />
  }

  return (
    <Workspace
      user={user}
      onLogout={async () => {
        try { await api.logout() } finally { setUser(null); setSession('guest') }
      }}
    />
  )
}

function LaunchScreen() {
  return (
    <main className="launch-screen" aria-label="Loading CaseLedger">
      <div className="launch-mark"><BrandMark size={48} /></div><span className="spinner" aria-hidden="true" />
      <span className="sr-only">Loading your workspace</span>
    </main>
  )
}

function Workspace({ user, onLogout }: { user: User; onLogout: () => Promise<void> }) {
  const [view, setView] = useState<View>('dashboard')
  const [selectedCaseId, setSelectedCaseId] = useState<string | null>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [mobileNavOpen, setMobileNavOpen] = useState(false)
  const [refreshKey, setRefreshKey] = useState(0)
  const [toast, setToast] = useState<{ kind: 'success' | 'danger'; message: string } | null>(null)

  useEffect(() => {
    if (!toast) return
    const timer = window.setTimeout(() => setToast(null), 4500)
    return () => window.clearTimeout(timer)
  }, [toast])

  const navigate = (nextView: View) => {
    setView(nextView)
    setMobileNavOpen(false)
  }
  const notify = (message: string, kind: 'success' | 'danger' = 'success') => setToast({ kind, message })
  const refresh = () => setRefreshKey((current) => current + 1)
  const navItems: { id: View; label: string; icon: IconName }[] = [
    { id: 'dashboard', label: 'Overview', icon: 'dashboard' },
    { id: 'cases', label: 'Cases', icon: 'briefcase' },
    { id: 'integrity', label: 'Integrity', icon: 'fingerprint' },
  ]

  return (
    <div className="app-shell">
      <aside className={`sidebar ${mobileNavOpen ? 'sidebar--open' : ''}`}>
        <div className="sidebar__brand">
          <a className="brand brand--light" href="/" onClick={(event) => { event.preventDefault(); navigate('dashboard') }}><BrandMark /><span>CaseLedger</span></a>
          <button className="icon-button sidebar__close" onClick={() => setMobileNavOpen(false)} aria-label="Close navigation"><Icon name="close" /></button>
        </div>
        <nav className="sidebar__nav" aria-label="Main navigation">
          <p className="nav-label">Workspace</p>
          {navItems.map((item) => (
            <button className={`nav-item ${view === item.id ? 'nav-item--active' : ''}`} key={item.id} onClick={() => navigate(item.id)}>
              <Icon name={item.icon} size={19} /><span>{item.label}</span>{view === item.id && <i />}
            </button>
          ))}
        </nav>
        <div className="sidebar__status">
          <div className="secure-status"><span className="secure-status__icon"><Icon name="shield" size={18} /></span><span><b>Ledger secured</b><small>Hash chain active</small></span><i /></div>
        </div>
        <div className="sidebar__profile">
          <span className="avatar">{initials(user.name)}</span><span className="profile-copy"><b>{user.name}</b><small>{titleCase(user.role)}</small></span>
          <button className="icon-button icon-button--dark" onClick={onLogout} aria-label="Sign out"><Icon name="logout" size={18} /></button>
        </div>
      </aside>
      {mobileNavOpen && <button className="nav-scrim" aria-label="Close navigation" onClick={() => setMobileNavOpen(false)} />}

      <div className="app-main">
        <header className="topbar">
          <button className="icon-button topbar__menu" onClick={() => setMobileNavOpen(true)} aria-label="Open navigation"><Icon name="menu" /></button>
          <div className="topbar__trail"><span>CaseLedger</span><i>/</i><b>{navItems.find((item) => item.id === view)?.label}</b></div>
          <div className="topbar__actions"><span className="topbar__secure"><i /> System operational</span><button className="topbar__profile" onClick={() => setMobileNavOpen(true)}><span className="avatar avatar--small">{initials(user.name)}</span><span>{user.name}</span><Icon name="chevron-down" size={14} /></button></div>
        </header>
        <main className="workspace-main">
          {view === 'dashboard' && <DashboardPage user={user} refreshKey={refreshKey} onCreate={() => setCreateOpen(true)} onSelectCase={setSelectedCaseId} onViewCases={() => navigate('cases')} />}
          {view === 'cases' && <CasesPage refreshKey={refreshKey} onCreate={() => setCreateOpen(true)} onSelectCase={setSelectedCaseId} />}
          {view === 'integrity' && <IntegrityPage refreshKey={refreshKey} onSelectCase={setSelectedCaseId} />}
        </main>
      </div>

      {selectedCaseId && <CaseDetailDrawer caseId={selectedCaseId} onClose={() => setSelectedCaseId(null)} onMutate={refresh} notify={notify} />}
      {createOpen && <CreateCaseModal onClose={() => setCreateOpen(false)} onCreated={(createdCase) => { setCreateOpen(false); refresh(); notify(`${createdCase.reference} was created.`); setSelectedCaseId(createdCase.id) }} notify={notify} />}
      {toast && <div className={`toast toast--${toast.kind}`} role="status"><span><Icon name={toast.kind === 'success' ? 'check' : 'warning'} size={17} /></span><p>{toast.message}</p><button onClick={() => setToast(null)} aria-label="Dismiss notification"><Icon name="close" size={16} /></button></div>}
    </div>
  )
}

export default App
