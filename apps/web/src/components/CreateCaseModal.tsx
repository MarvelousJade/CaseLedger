import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api, getErrorMessage } from '../api'
import { Icon } from '../Icons'
import type { CaseItem, CaseSeverity, CreateCaseInput, User } from '../types'

const severities: CaseSeverity[] = ['Low', 'Medium', 'High', 'Critical']
const categories = ['Fraud', 'Cybercrime', 'Compliance', 'Internal', 'Other']

interface CreateCaseModalProps {
  onClose: () => void
  onCreated: (item: CaseItem) => void
  notify: (message: string, kind?: 'success' | 'danger') => void
}

export function CreateCaseModal({ onClose, onCreated, notify }: CreateCaseModalProps) {
  const [title, setTitle] = useState('')
  const [summary, setSummary] = useState('')
  const [severity, setSeverity] = useState<CaseSeverity>('Medium')
  const [category, setCategory] = useState('Cybercrime')
  const [assigneeId, setAssigneeId] = useState('')
  const [dueAt, setDueAt] = useState('')
  const [tags, setTags] = useState('')
  const [users, setUsers] = useState<User[]>([])
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')
  const titleInput = useRef<HTMLInputElement>(null)

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', onKeyDown)
    document.body.classList.add('modal-open')
    window.setTimeout(() => titleInput.current?.focus(), 0)
    api.getUsers().then(setUsers).catch(() => setUsers([]))
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      document.body.classList.remove('modal-open')
    }
  }, [onClose])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSubmitting(true)
    setError('')
    const input: CreateCaseInput = {
      title: title.trim(),
      summary: summary.trim(),
      severity,
      category,
      tags: tags.split(',').map((tag) => tag.trim().replace(/^#/, '')).filter(Boolean),
      ...(assigneeId ? { assigneeId } : {}),
      ...(dueAt ? { dueAt: new Date(`${dueAt}T12:00:00`).toISOString() } : {}),
    }
    try {
      onCreated(await api.createCase(input))
    } catch (requestError) {
      const message = getErrorMessage(requestError, 'The case could not be created.')
      setError(message)
      notify(message, 'danger')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="modal-layer">
      <button className="modal-scrim" onClick={onClose} aria-label="Close new case form" />
      <section className="modal" role="dialog" aria-modal="true" aria-labelledby="create-case-title">
        <div className="modal__header">
          <div><span className="eyebrow">NEW INVESTIGATION</span><h2 id="create-case-title">Create a case</h2><p>Start a secure, traceable record for this investigation.</p></div>
          <button className="icon-button" onClick={onClose} aria-label="Close"><Icon name="close" /></button>
        </div>
        <form onSubmit={submit}>
          <div className="modal__body">
            {error && <div className="alert alert--danger" role="alert"><Icon name="warning" size={18} />{error}</div>}
            <label className="field"><span>Case title <i>Required</i></span><input ref={titleInput} value={title} onChange={(event) => setTitle(event.target.value)} placeholder="e.g. Credential exposure review" maxLength={120} required /></label>
            <label className="field"><span>Summary <i>Required</i></span><textarea rows={4} value={summary} onChange={(event) => setSummary(event.target.value)} placeholder="Describe what was reported and the initial scope…" maxLength={1200} required /><small className="field-count">{summary.length}/1200</small></label>
            <div className="form-grid">
              <label className="field"><span>Severity</span><div className="select-wrap select-wrap--field"><select value={severity} onChange={(event) => setSeverity(event.target.value)}>{severities.map((entry) => <option key={entry}>{entry}</option>)}</select><Icon name="chevron-down" size={15} /></div></label>
              <label className="field"><span>Category</span><div className="select-wrap select-wrap--field"><select value={category} onChange={(event) => setCategory(event.target.value)}>{categories.map((entry) => <option key={entry}>{entry}</option>)}</select><Icon name="chevron-down" size={15} /></div></label>
              <label className="field"><span>Assignee</span><div className="select-wrap select-wrap--field"><select value={assigneeId} onChange={(event) => setAssigneeId(event.target.value)}><option value="">Unassigned</option>{users.map((entry) => <option key={entry.id} value={entry.id}>{entry.name}</option>)}</select><Icon name="chevron-down" size={15} /></div></label>
              <label className="field"><span>Target date</span><input type="date" value={dueAt} onChange={(event) => setDueAt(event.target.value)} /></label>
            </div>
            <label className="field"><span>Tags <i>Optional</i></span><input value={tags} onChange={(event) => setTags(event.target.value)} placeholder="identity, external, priority" /><small>Separate tags with commas.</small></label>
          </div>
          <div className="modal__footer">
            <button className="button button--ghost" type="button" onClick={onClose}>Cancel</button>
            <button className="button button--primary" type="submit" disabled={submitting || !title.trim() || !summary.trim()}>{submitting ? <><span className="button-spinner" /> Creating…</> : <><Icon name="plus" size={17} /> Create case</>}</button>
          </div>
        </form>
      </section>
    </div>
  )
}

