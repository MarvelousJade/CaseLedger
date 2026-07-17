import type { ChangeEvent, FormEvent } from 'react'
import { Icon, type IconName } from '../Icons'
import type { AuditVerificationJob, CaseItem, IntegrityResult } from '../types'
import { formatBytes, formatDate, formatRelative, initials, titleCase } from '../utils'
import { EmptyState } from './Common'

const statuses = ['New', 'InProgress', 'Resolved']

export function OverviewSection({ item, status, setStatus, saveStatus, savingStatus, verify, verifying, integrity, verificationJob }: {
  item: CaseItem
  status: string
  setStatus: (value: string) => void
  saveStatus: () => void
  savingStatus: boolean
  verify: () => void
  verifying: boolean
  integrity: IntegrityResult | null
  verificationJob: AuditVerificationJob | null
}) {
  return (
    <div className="detail-section-stack">
      <section className="detail-section">
        <div className="detail-section__heading"><h3>Case summary</h3><span className="category-chip">{item.category}</span></div>
        <p className="case-summary">{item.summary || 'No summary has been added to this case.'}</p>
        {item.tags.length > 0 && <div className="tag-list">{item.tags.map((tag) => <span key={tag}>#{tag}</span>)}</div>}
      </section>
      <section className="detail-grid">
        <DetailFact icon="user" label="Assigned to" value={item.assigneeName || 'Unassigned'} />
        <DetailFact icon="user" label="Created by" value={item.createdByName} />
        <DetailFact icon="calendar" label="Created" value={formatDate(item.createdAt)} />
        <DetailFact icon="calendar" label="Due date" value={formatDate(item.dueAt)} />
      </section>
      <section className="detail-section status-control">
        <div><h3>Workflow status</h3><p>Keep the investigation stage current.</p></div>
        <div className="status-control__actions">
          <label className="select-wrap"><span className="sr-only">Case status</span><select value={status} onChange={(event) => setStatus(event.target.value)}>{statuses.map((entry) => <option key={entry} value={entry}>{titleCase(entry)}</option>)}</select><Icon name="chevron-down" size={15} /></label>
          <button className="button button--secondary" disabled={savingStatus || status === item.status} onClick={saveStatus}>{savingStatus ? 'Saving…' : 'Update'}</button>
        </div>
      </section>
      <section className="verify-card">
        <span className="verify-card__icon"><Icon name="fingerprint" size={24} /></span>
        <div><h3>Verify activity chain</h3><p>Recalculate every event hash and confirm this record has not been altered.</p></div>
        <button className="button button--dark" onClick={verify} disabled={verifying}>{verifying ? <><span className="button-spinner" /> Verifying…</> : verificationJob && !verificationJob.isCurrent ? 'Verify again' : 'Verify now'}</button>
        <VerificationStatus job={verificationJob} integrity={integrity} />
      </section>
    </div>
  )
}

function VerificationStatus({ job, integrity }: {
  job: AuditVerificationJob | null
  integrity: IntegrityResult | null
}) {
  if (!job) {
    if (!integrity) return null
    return (
      <div
        className={`verify-result ${integrity.valid ? 'verify-result--valid' : 'verify-result--invalid'}`}
        role="status"
        aria-live="polite"
      >
        <Icon name={integrity.valid ? 'check' : 'warning'} size={17} />
        <span>{integrity.valid
          ? `Verified · ${integrity.checkedEvents} events checked`
          : `Integrity break detected${integrity.brokenAt ? ` at ${integrity.brokenAt}` : ''}`}</span>
      </div>
    )
  }

  const status = job.status.toLowerCase()
  if (status === 'queued' || status === 'processing') {
    return (
      <div className="verify-result verify-result--pending" role="status" aria-live="polite">
        <span className="button-spinner" />
        <span>Verification queued · {job.targetSequence} events in snapshot</span>
      </div>
    )
  }

  if (status === 'completed' && job.valid === true && !job.isCurrent) {
    return (
      <div className="verify-result verify-result--stale" role="status" aria-live="polite">
        <Icon name="warning" size={17} />
        <span>Verified snapshot is outdated · Run again for the current chain</span>
      </div>
    )
  }

  if (status === 'completed' && job.valid === true) {
    return (
      <div className="verify-result verify-result--valid" role="status" aria-live="polite">
        <Icon name="check" size={17} />
        <span>Verified · {job.checkedEvents ?? job.targetSequence} events checked</span>
      </div>
    )
  }

  if (status === 'completed' && job.valid === false) {
    return (
      <div className="verify-result verify-result--invalid" role="status" aria-live="assertive">
        <Icon name="warning" size={17} />
        <span>Integrity break detected{job.brokenAt ? ` at event ${job.brokenAt}` : ''}</span>
      </div>
    )
  }

  return (
    <div className="verify-result verify-result--invalid" role="status" aria-live="assertive">
      <Icon name="warning" size={17} />
      <span>Verification could not finish · Retry to start a fresh verification</span>
    </div>
  )
}

export function ActivitySection({ item, comment, setComment, addComment, commenting }: {
  item: CaseItem
  comment: string
  setComment: (value: string) => void
  addComment: (event: FormEvent) => void
  commenting: boolean
}) {
  return (
    <div className="detail-section-stack">
      <form className="comment-form" onSubmit={addComment}>
        <label htmlFor="case-comment">Add a case note</label>
        <textarea id="case-comment" rows={3} placeholder="Record an observation, decision, or next step…" value={comment} onChange={(event) => setComment(event.target.value)} maxLength={1000} />
        <div><span>{comment.length}/1000</span><button className="button button--primary" disabled={commenting || !comment.trim()}>{commenting ? 'Adding…' : <><Icon name="comment" size={16} /> Add note</>}</button></div>
      </form>
      {item.activity.length ? (
        <div className="timeline">{item.activity.map((activity, index) => {
          const isComment = activity.eventType.toLowerCase().includes('comment')
          return (
            <article className="timeline-item" key={activity.id || `${activity.createdAt}-${index}`}>
              <span className={`timeline-icon ${isComment ? 'timeline-icon--comment' : ''}`}><Icon name={isComment ? 'comment' : 'activity'} size={16} /></span>
              <div><div className="timeline-item__top"><b>{titleCase(activity.eventType)}</b><time>{formatRelative(activity.createdAt)}</time></div><p>{activity.description || 'Case record updated.'}</p><span className="timeline-actor"><span className="avatar avatar--micro">{initials(activity.actorName)}</span>{activity.actorName} · {formatDate(activity.createdAt, true)}</span></div>
            </article>
          )
        })}</div>
      ) : <EmptyState compact icon="activity" title="No activity yet" message="Updates and case notes will appear in this secure trail." />}
    </div>
  )
}

export function EvidenceSection({ item, addEvidence, uploading, uploadMessage }: {
  item: CaseItem
  addEvidence: (event: ChangeEvent<HTMLInputElement>) => void
  uploading: boolean
  uploadMessage: string
}) {
  return (
    <div className="detail-section-stack">
      <label className={`evidence-drop ${uploading ? 'evidence-drop--busy' : ''}`}>
        <input type="file" onChange={addEvidence} disabled={uploading} />
        <span className="evidence-drop__icon">{uploading ? <span className="spinner" /> : <Icon name="upload" size={23} />}</span>
        <span><b>{uploading ? uploadMessage : 'Fingerprint a file'}</b><small>{uploading ? 'Keep this panel open while the hash is calculated.' : 'Select any file to calculate and register its SHA-256 digest.'}</small></span>
      </label>
      <p className="evidence-note"><Icon name="shield" size={14} /> Only file metadata and its cryptographic fingerprint are recorded.</p>
      {item.evidence.length ? (
        <div className="evidence-list">{item.evidence.map((evidence) => (
          <article key={evidence.id}>
            <span className="evidence-file-icon"><Icon name="document" size={20} /></span>
            <div className="evidence-file-main"><div><b>{evidence.fileName}</b><span>{formatBytes(evidence.sizeBytes)} · {evidence.mediaType}</span></div><code title={evidence.sha256}>{evidence.sha256}</code><small>Added by {evidence.addedByName} · {formatDate(evidence.createdAt, true)}</small></div>
            <span className="verified-mini"><Icon name="check" size={12} /> Hashed</span>
          </article>
        ))}</div>
      ) : <EmptyState compact icon="document" title="No evidence registered" message="Fingerprint a file to preserve its identity in this case." />}
    </div>
  )
}

function DetailFact({ icon, label, value }: { icon: IconName; label: string; value: string }) {
  return <div className="detail-fact"><span><Icon name={icon} size={17} /></span><div><small>{label}</small><b>{value}</b></div></div>
}
