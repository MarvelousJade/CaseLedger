import { useQuery } from '@apollo/client/react'
import { useState } from 'react'
import { Icon } from '../Icons'
import { Assignee, CaseTableSkeleton, EmptyState, ErrorState, PageHeading, SeverityBadge, StatusBadge } from '../components/Common'
import {
  GatewayCasesDocument,
  type CaseSeverity,
  type CaseSortField,
  type CaseStatus,
  type GatewayCasesQuery,
  type SortDirection,
} from '../graphql/generated/graphql'
import { formatDate, formatRelative, titleCase, useDebouncedValue } from '../utils'

const pageSize = 10

type GatewayCase = GatewayCasesQuery['cases']['nodes'][number]

export function GatewayCasesPage({
  onSelectCase,
}: {
  onSelectCase: (id: string) => void
}) {
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<CaseStatus | ''>('')
  const [severity, setSeverity] = useState<CaseSeverity | ''>('')
  const [sortField, setSortField] = useState<CaseSortField>('UPDATED_AT')
  const [sortDirection, setSortDirection] = useState<SortDirection>('DESC')
  const [page, setPage] = useState(1)
  const debouncedSearch = useDebouncedValue(search, 300)
  const { data, error, loading, refetch } = useQuery(GatewayCasesDocument, {
    notifyOnNetworkStatusChange: true,
    variables: {
      filter: {
        search: debouncedSearch || null,
        severity: severity || null,
        status: status || null,
      },
      pagination: { page, pageSize },
      sort: { direction: sortDirection, field: sortField },
    },
  })
  const collection = data?.cases
  const hasFilters = Boolean(search || status || severity)
  const resetPage = () => setPage(1)
  const clearFilters = () => {
    setSearch('')
    setStatus('')
    setSeverity('')
    resetPage()
  }

  return (
    <div className="page cases-page">
      <PageHeading
        eyebrow="NODE.JS GRAPHQL GATEWAY"
        title="GraphQL cases"
        description="Explore the same case ledger through typed, client-facing aggregation."
        action={<button className="button button--secondary" onClick={() => void refetch()}><Icon name="refresh" size={17} /> Refresh</button>}
      />
      <div className="notice"><Icon name="hash" size={17} /><span>JWT-authorized GraphQL with server-side filtering, sorting, pagination, and batched investigator reads.</span></div>
      <section className="panel cases-panel">
        <div className="case-toolbar">
          <label className="search-field">
            <span className="sr-only">Search GraphQL cases</span><Icon name="search" size={18} />
            <input
              placeholder="Search title, reference, or summary…"
              value={search}
              onChange={(event) => { setSearch(event.target.value); resetPage() }}
            />
            {search && <button type="button" onClick={() => { setSearch(''); resetPage() }} aria-label="Clear search"><Icon name="close" size={15} /></button>}
          </label>
          <div className="filter-group">
            <FilterSelect
              label="Filter by status"
              value={status}
              onChange={(value) => { setStatus(value as CaseStatus | ''); resetPage() }}
              options={['NEW', 'IN_PROGRESS', 'RESOLVED']}
              emptyLabel="All statuses"
            />
            <FilterSelect
              label="Filter by severity"
              value={severity}
              onChange={(value) => { setSeverity(value as CaseSeverity | ''); resetPage() }}
              options={['LOW', 'MEDIUM', 'HIGH', 'CRITICAL']}
              emptyLabel="All severity"
            />
            <FilterSelect
              label="Sort cases"
              value={sortField}
              onChange={(value) => { setSortField(value as CaseSortField); resetPage() }}
              options={['UPDATED_AT', 'TITLE', 'SEVERITY', 'DUE_AT']}
            />
            <button
              className="button button--ghost"
              type="button"
              onClick={() => { setSortDirection((current) => current === 'ASC' ? 'DESC' : 'ASC'); resetPage() }}
            >
              {sortDirection === 'ASC' ? 'Ascending' : 'Descending'}
            </button>
          </div>
        </div>
        <div className="case-count" aria-live="polite">
          <span>{loading && !collection ? 'Loading cases…' : `${collection?.pageInfo.totalCount ?? 0} ${(collection?.pageInfo.totalCount ?? 0) === 1 ? 'case' : 'cases'}`}</span>
          {hasFilters && <button type="button" onClick={clearFilters}>Clear filters</button>}
        </div>
        {loading && !collection
          ? <CaseTableSkeleton />
          : error
            ? <ErrorState message={error.message} onRetry={() => void refetch()} compact />
            : collection?.nodes.length
              ? <GatewayCaseTable items={collection.nodes} onSelect={onSelectCase} />
              : <EmptyState compact icon={hasFilters ? 'search' : 'folder'} title={hasFilters ? 'No matching cases' : 'No cases available'} message={hasFilters ? 'Try another search or clear the filters.' : 'The gateway returned an empty case ledger.'} />}
        {collection && collection.pageInfo.totalPages > 1 && (
          <nav className="case-pagination" aria-label="GraphQL case pages">
            <span className="case-pagination__summary">Page {collection.pageInfo.page} of {collection.pageInfo.totalPages}</span>
            <div className="case-pagination__controls">
              <button className="button button--ghost" disabled={loading || !collection.pageInfo.hasPreviousPage} onClick={() => setPage((current) => current - 1)}>Previous</button>
              <button className="button button--ghost" disabled={loading || !collection.pageInfo.hasNextPage} onClick={() => setPage((current) => current + 1)}>Next <Icon name="arrow-right" size={15} /></button>
            </div>
          </nav>
        )}
      </section>
    </div>
  )
}

function FilterSelect({
  emptyLabel,
  label,
  onChange,
  options,
  value,
}: {
  emptyLabel?: string
  label: string
  onChange: (value: string) => void
  options: string[]
  value: string
}) {
  return (
    <label className="select-wrap">
      <span className="sr-only">{label}</span>
      <select value={value} onChange={(event) => onChange(event.target.value)}>
        {emptyLabel && <option value="">{emptyLabel}</option>}
        {options.map((option) => <option value={option} key={option}>{enumLabel(option)}</option>)}
      </select>
      <Icon name="chevron-down" size={15} />
    </label>
  )
}

function GatewayCaseTable({
  items,
  onSelect,
}: {
  items: GatewayCase[]
  onSelect: (id: string) => void
}) {
  return (
    <div className="case-table-wrap">
      <table className="case-table">
        <thead><tr><th>Case</th><th>Status</th><th>Severity</th><th>Investigator</th><th>Last updated</th><th><span className="sr-only">Open case</span></th></tr></thead>
        <tbody>{items.map((item) => (
          <tr key={item.id}>
            <td><button className="case-title-button" onClick={() => onSelect(item.id)}><span className={`case-glyph severity-${item.severity.toLowerCase()}`}><Icon name="document" size={18} /></span><span><b>{item.title}</b><small>{item.reference} · {item.category}</small></span></button></td>
            <td><StatusBadge value={enumLabel(item.status)} /></td>
            <td><SeverityBadge value={enumLabel(item.severity)} /></td>
            <td><Assignee name={item.investigator?.name} /></td>
            <td><span className="date-cell">{formatRelative(item.updatedAt)}<small>{formatDate(item.updatedAt)}</small></span></td>
            <td><button className="row-action" onClick={() => onSelect(item.id)} aria-label={`Open ${item.title}`}><Icon name="arrow-right" size={17} /></button></td>
          </tr>
        ))}</tbody>
      </table>
    </div>
  )
}

function enumLabel(value: string): string {
  return titleCase(value.toLowerCase())
}
