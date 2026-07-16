import { useEffect, useState } from 'react'
import { api, getErrorMessage } from '../api'
import { Icon } from '../Icons'
import type { CaseCollection, CaseItem } from '../types'
import { formatDate, formatRelative, titleCase, useDebouncedValue } from '../utils'
import { Assignee, CaseTableSkeleton, EmptyState, ErrorState, PageHeading, SeverityBadge, StatusBadge } from '../components/Common'

const statuses = ['New', 'InProgress', 'Resolved']
const severities = ['Low', 'Medium', 'High', 'Critical']

export function CasesPage({ refreshKey, onCreate, onSelectCase }: { refreshKey: number; onCreate: () => void; onSelectCase: (id: string) => void }) {
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState('')
  const [severity, setSeverity] = useState('')
  const [collection, setCollection] = useState<CaseCollection>({ items: [], total: 0 })
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [reloadKey, setReloadKey] = useState(0)
  const debouncedSearch = useDebouncedValue(search, 300)

  useEffect(() => {
    let active = true
    setLoading(true)
    setError('')
    api.getCases({ search: debouncedSearch, status, severity })
      .then((result) => { if (active) setCollection(result) })
      .catch((requestError) => { if (active) setError(getErrorMessage(requestError, 'Cases could not be loaded.')) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [debouncedSearch, status, severity, refreshKey, reloadKey])

  const hasFilters = Boolean(search || status || severity)
  const clearFilters = () => { setSearch(''); setStatus(''); setSeverity('') }

  return (
    <div className="page cases-page">
      <PageHeading
        eyebrow="INVESTIGATION WORKSPACE"
        title="Cases"
        description="Track active investigations and preserve every decision."
        action={<button className="button button--primary" onClick={onCreate}><Icon name="plus" size={18} /> New case</button>}
      />
      <section className="panel cases-panel">
        <div className="case-toolbar">
          <label className="search-field">
            <span className="sr-only">Search cases</span><Icon name="search" size={18} />
            <input placeholder="Search title, reference, or assignee…" value={search} onChange={(event) => setSearch(event.target.value)} />
            {search && <button onClick={() => setSearch('')} aria-label="Clear search"><Icon name="close" size={15} /></button>}
          </label>
          <div className="filter-group">
            <label className="select-wrap"><span className="sr-only">Filter by status</span><select value={status} onChange={(event) => setStatus(event.target.value)}><option value="">All statuses</option>{statuses.map((item) => <option key={item} value={item}>{titleCase(item)}</option>)}</select><Icon name="chevron-down" size={15} /></label>
            <label className="select-wrap"><span className="sr-only">Filter by severity</span><select value={severity} onChange={(event) => setSeverity(event.target.value)}><option value="">All severity</option>{severities.map((item) => <option key={item} value={item}>{item}</option>)}</select><Icon name="chevron-down" size={15} /></label>
          </div>
        </div>
        <div className="case-count"><span>{loading ? 'Loading cases…' : `${collection.total} ${collection.total === 1 ? 'case' : 'cases'}`}</span>{hasFilters && <button onClick={clearFilters}>Clear filters</button>}</div>
        {loading ? <CaseTableSkeleton /> : error ? <ErrorState message={error} onRetry={() => setReloadKey((key) => key + 1)} compact /> : collection.items.length ? <CaseTable items={collection.items} onSelect={onSelectCase} /> : <EmptyState icon={hasFilters ? 'search' : 'folder'} title={hasFilters ? 'No matching cases' : 'Your ledger is ready'} message={hasFilters ? 'Try a different search term or clear a filter.' : 'Create your first case to start a secure activity trail.'} action={hasFilters ? <button className="button button--secondary" onClick={clearFilters}>Clear filters</button> : <button className="button button--primary" onClick={onCreate}><Icon name="plus" size={17} /> Create case</button>} />}
      </section>
    </div>
  )
}

function CaseTable({ items, onSelect }: { items: CaseItem[]; onSelect: (id: string) => void }) {
  return (
    <div className="case-table-wrap">
      <table className="case-table">
        <thead><tr><th>Case</th><th>Status</th><th>Severity</th><th>Assignee</th><th>Last updated</th><th><span className="sr-only">Open case</span></th></tr></thead>
        <tbody>{items.map((item) => (
          <tr key={item.id}>
            <td><button className="case-title-button" onClick={() => onSelect(item.id)}><span className={`case-glyph severity-${item.severity.toLowerCase()}`}><Icon name="document" size={18} /></span><span><b>{item.title}</b><small>{item.reference} · {item.category}</small></span></button></td>
            <td><StatusBadge value={item.status} /></td><td><SeverityBadge value={item.severity} /></td><td><Assignee name={item.assigneeName} /></td>
            <td><span className="date-cell">{formatRelative(item.updatedAt)}<small>{formatDate(item.updatedAt)}</small></span></td>
            <td><button className="row-action" onClick={() => onSelect(item.id)} aria-label={`Open ${item.title}`}><Icon name="arrow-right" size={17} /></button></td>
          </tr>
        ))}</tbody>
      </table>
      <div className="case-card-list">{items.map((item) => (
        <button className="case-mobile-card" key={item.id} onClick={() => onSelect(item.id)}>
          <span className="case-mobile-card__top"><span className={`case-glyph severity-${item.severity.toLowerCase()}`}><Icon name="document" size={18} /></span><span><b>{item.title}</b><small>{item.reference} · {item.category}</small></span><Icon name="arrow-right" size={17} /></span>
          <span className="case-mobile-card__meta"><StatusBadge value={item.status} /><SeverityBadge value={item.severity} /><span>{formatRelative(item.updatedAt)}</span></span>
        </button>
      ))}</div>
    </div>
  )
}

