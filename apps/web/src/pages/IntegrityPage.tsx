import { useEffect, useState } from 'react'
import { api, getErrorMessage } from '../api'
import { Icon } from '../Icons'
import type { CaseItem } from '../types'
import { formatRelative } from '../utils'
import { CaseTableSkeleton, EmptyState, ErrorState, PageHeading } from '../components/Common'

export function IntegrityPage({ refreshKey, onSelectCase }: { refreshKey: number; onSelectCase: (id: string) => void }) {
  const [cases, setCases] = useState<CaseItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    let active = true
    setLoading(true)
    setError('')
    api.getCases()
      .then((result) => { if (active) setCases(result.items) })
      .catch((requestError) => { if (active) setError(getErrorMessage(requestError)) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [refreshKey])

  return (
    <div className="page integrity-page">
      <PageHeading eyebrow="TRUST & VERIFICATION" title="Ledger integrity" description="Review cryptographic evidence and verify each case history." />
      <section className="integrity-hero">
        <div className="integrity-hero__icon"><Icon name="fingerprint" size={38} /></div>
        <div><span className="eyebrow eyebrow--light">SHA-256 EVENT CHAIN</span><h2>Every change is independently verifiable.</h2><p>Case activity and evidence fingerprints form a tamper-evident record. Open any case below to verify its complete history.</p></div>
        <div className="integrity-hero__metric"><strong>{loading ? '—' : cases.length}</strong><span>Ledgers monitored</span></div>
      </section>
      <section className="panel integrity-list-panel">
        <div className="panel__header"><div><h2>Case ledgers</h2><p>Run verification from an individual case record</p></div><span className="live-chip"><i /> Monitoring</span></div>
        {loading ? <CaseTableSkeleton /> : error ? <ErrorState message={error} /> : cases.length ? (
          <div className="integrity-case-list">{cases.map((item) => (
            <button key={item.id} onClick={() => onSelectCase(item.id)}>
              <span className="integrity-case-list__shield"><Icon name="shield" size={19} /></span>
              <span><b>{item.title}</b><small>{item.reference} · Updated {formatRelative(item.updatedAt)}</small></span>
              <span className="integrity-ready"><i /> Ready to verify</span><Icon name="arrow-right" size={17} />
            </button>
          ))}</div>
        ) : <EmptyState icon="shield" title="No ledgers to verify" message="Case ledgers will appear here after a case is created." />}
      </section>
    </div>
  )
}

