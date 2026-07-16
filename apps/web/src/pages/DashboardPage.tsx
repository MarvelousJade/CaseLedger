import { useEffect, useState } from 'react'
import { api, getErrorMessage } from '../api'
import { Icon, type IconName } from '../Icons'
import type { DashboardData, User } from '../types'
import { formatRelative, titleCase } from '../utils'
import { EmptyState, ErrorState, PageHeading, SeverityBadge, StatusBadge } from '../components/Common'

interface DashboardPageProps {
  user: User
  refreshKey: number
  onCreate: () => void
  onSelectCase: (id: string) => void
  onViewCases: () => void
}

export function DashboardPage({ user, refreshKey, onCreate, onSelectCase, onViewCases }: DashboardPageProps) {
  const [data, setData] = useState<DashboardData | null>(null)
  const [loading, setLoading] = useState(true)
  const [degraded, setDegraded] = useState(false)
  const [error, setError] = useState('')
  const [reloadKey, setReloadKey] = useState(0)

  useEffect(() => {
    let active = true
    setLoading(true)
    setError('')
    Promise.allSettled([api.getDashboard(), api.getCases()]).then(([dashboardResult, casesResult]) => {
      if (!active) return
      const cases = casesResult.status === 'fulfilled' ? casesResult.value.items : []
      if (dashboardResult.status === 'fulfilled') {
        setData({
          ...dashboardResult.value,
          recentCases: dashboardResult.value.recentCases.length ? dashboardResult.value.recentCases : cases.slice(0, 5),
        })
        setDegraded(false)
      } else if (casesResult.status === 'fulfilled') {
        setData({
          totalCases: casesResult.value.total,
          openCases: cases.filter((item) => item.status !== 'Resolved').length,
          criticalCases: cases.filter((item) => item.severity === 'Critical').length,
          resolvedCases: cases.filter((item) => item.status === 'Resolved').length,
          integrityStatus: 'Unavailable',
          recentCases: cases.slice(0, 5),
        })
        setDegraded(true)
      } else {
        setError(getErrorMessage(dashboardResult.reason, 'The overview could not be loaded.'))
      }
      setLoading(false)
    })
    return () => { active = false }
  }, [refreshKey, reloadKey])

  const firstName = user.name.split(' ')[0]
  const stats = data ? [
    { label: 'Total cases', value: data.totalCases, detail: 'Across your workspace', icon: 'briefcase' as IconName, tone: 'ink' },
    { label: 'Active workload', value: data.openCases, detail: 'New or in progress', icon: 'activity' as IconName, tone: 'blue' },
    { label: 'Critical priority', value: data.criticalCases, detail: 'Needs close attention', icon: 'warning' as IconName, tone: 'red' },
    { label: 'Resolved', value: data.resolvedCases, detail: 'Successfully completed', icon: 'check' as IconName, tone: 'green' },
  ] : []
  const greeting = new Date().getHours() < 12 ? 'morning' : new Date().getHours() < 18 ? 'afternoon' : 'evening'

  return (
    <div className="page dashboard-page">
      <PageHeading
        eyebrow={new Intl.DateTimeFormat('en-CA', { weekday: 'long', month: 'long', day: 'numeric' }).format(new Date()).toUpperCase()}
        title={`Good ${greeting}, ${firstName}.`}
        description="Here is what is happening across your casework."
        action={<button className="button button--primary" onClick={onCreate}><Icon name="plus" size={18} /> New case</button>}
      />

      {degraded && <div className="notice"><Icon name="activity" size={17} /><span>Live dashboard metrics are unavailable. Showing a current case rollup.</span></div>}
      {loading ? <DashboardSkeleton /> : error ? <ErrorState message={error} onRetry={() => setReloadKey((key) => key + 1)} /> : data && <>
        <section className="stat-grid" aria-label="Case statistics">
          {stats.map((stat) => (
            <article className={`stat-card stat-card--${stat.tone}`} key={stat.label}>
              <div className="stat-card__top"><span className="stat-icon"><Icon name={stat.icon} /></span><span className="stat-pulse"><i /> Live</span></div>
              <strong>{stat.value.toLocaleString()}</strong><h2>{stat.label}</h2><p>{stat.detail}</p>
            </article>
          ))}
        </section>

        <section className="dashboard-grid">
          <div className="panel recent-panel">
            <div className="panel__header">
              <div><h2>Recent cases</h2><p>Latest activity across your workspace</p></div>
              <button className="button button--text" onClick={onViewCases}>View all <Icon name="arrow-right" size={16} /></button>
            </div>
            {data.recentCases.length ? (
              <div className="recent-list">
                {data.recentCases.slice(0, 5).map((item) => (
                  <button className="recent-case" key={item.id} onClick={() => onSelectCase(item.id)}>
                    <span className={`case-glyph severity-${item.severity.toLowerCase()}`}><Icon name="document" size={19} /></span>
                    <span className="recent-case__main"><span><b>{item.title}</b><small>{item.reference}</small></span><span className="recent-case__meta"><StatusBadge value={item.status} /><SeverityBadge value={item.severity} /></span></span>
                    <span className="recent-case__time">{formatRelative(item.updatedAt)}</span><Icon className="recent-case__arrow" name="arrow-right" size={17} />
                  </button>
                ))}
              </div>
            ) : <EmptyState compact icon="folder" title="No cases yet" message="Create the first case to begin building a trusted record." action={<button className="button button--secondary" onClick={onCreate}><Icon name="plus" size={17} /> Create case</button>} />}
          </div>

          <aside className="dashboard-side">
            <div className="panel integrity-card">
              <div className="integrity-orbit"><span><Icon name="shield" size={28} /></span><i /><i /><i /></div>
              <span className="eyebrow">Ledger integrity</span>
              <h2>{data.integrityStatus === 'Unavailable' ? 'Verification pending' : titleCase(data.integrityStatus)}</h2>
              <p>{data.integrityStatus === 'Unavailable' ? 'Open a case to verify its activity chain.' : 'The cryptographic activity chain is reporting healthy.'}</p>
              <div className="integrity-foot"><span><i /> Chain monitoring active</span><Icon name="fingerprint" size={19} /></div>
            </div>
            <div className="panel quick-card">
              <div className="quick-card__icon"><Icon name="sparkle" /></div>
              <div><h3>Quick action</h3><p>Capture a new report while the details are fresh.</p></div>
              <button className="button button--secondary button--full" onClick={onCreate}>Start a case <Icon name="arrow-right" size={17} /></button>
            </div>
          </aside>
        </section>
      </>}
    </div>
  )
}

function DashboardSkeleton() {
  return <>
    <div className="stat-grid">{Array.from({ length: 4 }).map((_, index) => <div className="stat-card skeleton-card" key={index}><span className="skeleton skeleton--square" /><span className="skeleton skeleton--number" /><span className="skeleton skeleton--line" /><span className="skeleton skeleton--short" /></div>)}</div>
    <div className="dashboard-grid"><div className="panel loading-panel"><span className="skeleton skeleton--heading" />{Array.from({ length: 4 }).map((_, index) => <span className="skeleton skeleton--row" key={index} />)}</div><div className="panel loading-panel"><span className="skeleton skeleton--heading" /><span className="skeleton skeleton--block" /></div></div>
  </>
}

