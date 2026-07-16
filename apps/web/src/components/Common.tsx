import type { ReactNode } from 'react'
import { Icon, type IconName } from '../Icons'
import { initials, titleCase } from '../utils'

export function PageHeading({ eyebrow, title, description, action }: { eyebrow: string; title: string; description: string; action?: ReactNode }) {
  return (
    <div className="page-heading">
      <div><span className="eyebrow">{eyebrow}</span><h1>{title}</h1><p>{description}</p></div>
      {action}
    </div>
  )
}

export function StatusBadge({ value }: { value: string }) {
  const tone = value.toLowerCase().replace(/[^a-z]/g, '')
  return <span className={`badge status-${tone}`}><i />{titleCase(value)}</span>
}

export function SeverityBadge({ value }: { value: string }) {
  return <span className={`severity-badge severity-badge--${value.toLowerCase()}`}><i />{titleCase(value)}</span>
}

export function Assignee({ name }: { name?: string }) {
  return name
    ? <span className="assignee"><span className="avatar avatar--tiny">{initials(name)}</span>{name}</span>
    : <span className="unassigned">Unassigned</span>
}

export function ErrorState({ message, onRetry, compact = false }: { message: string; onRetry?: () => void; compact?: boolean }) {
  return (
    <div className={`state-card state-card--error ${compact ? 'state-card--compact' : ''}`}>
      <span><Icon name="warning" size={22} /></span>
      <div><h3>We hit a snag</h3><p>{message}</p></div>
      {onRetry && <button className="button button--secondary" onClick={onRetry}><Icon name="refresh" size={16} /> Try again</button>}
    </div>
  )
}

export function EmptyState({ icon, title, message, action, compact = false }: { icon: IconName; title: string; message: string; action?: ReactNode; compact?: boolean }) {
  return (
    <div className={`empty-state ${compact ? 'empty-state--compact' : ''}`}>
      <span><Icon name={icon} size={24} /></span><h3>{title}</h3><p>{message}</p>{action}
    </div>
  )
}

export function CaseTableSkeleton() {
  return (
    <div className="table-skeleton">
      {Array.from({ length: 6 }).map((_, index) => <div key={index}><span className="skeleton skeleton--avatar" /><span className="skeleton skeleton--cell-wide" /><span className="skeleton skeleton--pill" /><span className="skeleton skeleton--pill" /><span className="skeleton skeleton--cell" /></div>)}
    </div>
  )
}

