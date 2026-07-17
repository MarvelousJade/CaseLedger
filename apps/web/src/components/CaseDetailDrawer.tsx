import { useCallback, useEffect, useRef, useState, type ChangeEvent, type FormEvent } from 'react'
import { api, getErrorMessage, isNotFound, isPreconditionFailed, isServiceUnavailable } from '../api'
import { Icon } from '../Icons'
import { subscribeToCaseUpdates } from '../realtime'
import type { AuditVerificationJob, CaseItem, IntegrityResult } from '../types'
import { formatRelative, titleCase } from '../utils'
import { ErrorState, SeverityBadge, StatusBadge } from './Common'
import { ActivitySection, EvidenceSection, OverviewSection } from './CaseDetailSections'

type DetailTab = 'overview' | 'activity' | 'evidence'

interface CaseDetailDrawerProps {
  caseId: string
  onClose: () => void
  onMutate: () => void
  notify: (message: string, kind?: 'success' | 'danger') => void
}

export function CaseDetailDrawer({ caseId, onClose, onMutate, notify }: CaseDetailDrawerProps) {
  const [item, setItem] = useState<CaseItem | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [tab, setTab] = useState<DetailTab>('overview')
  const [comment, setComment] = useState('')
  const [commenting, setCommenting] = useState(false)
  const [status, setStatus] = useState('')
  const [savingStatus, setSavingStatus] = useState(false)
  const [uploading, setUploading] = useState(false)
  const [uploadMessage, setUploadMessage] = useState('')
  const [integrity, setIntegrity] = useState<IntegrityResult | null>(null)
  const [verificationJob, setVerificationJob] = useState<AuditVerificationJob | null>(null)
  const [verifying, setVerifying] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const closeButton = useRef<HTMLButtonElement>(null)
  const announcedResult = useRef('')
  const notifyRef = useRef(notify)
  notifyRef.current = notify

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', onKeyDown)
    document.body.classList.add('modal-open')
    window.setTimeout(() => closeButton.current?.focus(), 0)
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      document.body.classList.remove('modal-open')
    }
  }, [onClose])

  useEffect(() => {
    let active = true
    setLoading(true)
    setError('')
    api.getCase(caseId)
      .then((result) => {
        if (!active) return
        setItem(result)
        setStatus(result.status)
      })
      .catch((requestError) => { if (active) setError(getErrorMessage(requestError, 'This case could not be loaded.')) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [caseId, reloadKey])

  const storeVerificationJob = useCallback((job: AuditVerificationJob, announce = false) => {
    setVerificationJob(job)
    if (!announce || !isTerminalVerification(job)) return

    const resultKey = job.resultId || `${job.id}:${job.status}:${job.completedAt ?? ''}`
    if (announcedResult.current === resultKey) return
    announcedResult.current = resultKey

    if (job.status.toLowerCase() === 'completed' && job.valid === true) {
      notifyRef.current(
        job.isCurrent
          ? `Integrity verified across ${job.checkedEvents ?? job.targetSequence} events.`
          : 'The verified snapshot is valid, but the case has changed since it was queued.',
        job.isCurrent ? 'success' : 'danger',
      )
    } else if (job.status.toLowerCase() === 'completed' && job.valid === false) {
      notifyRef.current('A break was detected in the activity chain.', 'danger')
    } else {
      notifyRef.current('Verification could not finish. Start a fresh verification to retry.', 'danger')
    }
  }, [])

  useEffect(() => {
    let active = true
    announcedResult.current = ''
    setVerificationJob(null)
    setIntegrity(null)
    api.getLatestAuditVerification(caseId)
      .then((job) => { if (active) storeVerificationJob(job) })
      .catch((requestError) => {
        if (active && !isNotFound(requestError)) setVerificationJob(null)
      })
    return () => { active = false }
  }, [caseId, reloadKey, storeVerificationJob])

  useEffect(() => {
    let active = true
    let unsubscribe: (() => Promise<void>) | undefined

    subscribeToCaseUpdates(caseId, (update) => {
      if (!active || update.caseId.toLowerCase() !== caseId.toLowerCase()) return
      void api.getAuditVerification(caseId, update.jobId)
        .then((job) => { if (active) storeVerificationJob(job, true) })
        .catch(() => undefined)
    })
      .then((stop) => {
        if (!active) void stop()
        else unsubscribe = stop
      })
      .catch(() => undefined)

    return () => {
      active = false
      if (unsubscribe) void unsubscribe()
    }
  }, [caseId, storeVerificationJob])

  useEffect(() => {
    if (!verificationJob || !isPendingVerification(verificationJob)) return
    let active = true
    const poll = window.setInterval(() => {
      void api.getAuditVerification(caseId, verificationJob.id)
        .then((job) => {
          if (active) storeVerificationJob(job, true)
        })
        .catch(() => undefined)
    }, 2_000)
    return () => {
      active = false
      window.clearInterval(poll)
    }
  }, [caseId, storeVerificationJob, verificationJob])

  const reload = () => {
    setReloadKey((key) => key + 1)
    onMutate()
  }

  const saveStatus = async () => {
    if (!item || status === item.status) return
    setSavingStatus(true)
    try {
      await api.updateCase(item.id, item.version, { status })
      notify(`Status changed to ${titleCase(status)}.`)
      reload()
    } catch (requestError) {
      if (isPreconditionFailed(requestError)) {
        notify('This case changed since you opened it. The latest details have been reloaded.', 'danger')
        reload()
      } else {
        notify(getErrorMessage(requestError, 'Status could not be updated.'), 'danger')
      }
    } finally {
      setSavingStatus(false)
    }
  }

  const addComment = async (event: FormEvent) => {
    event.preventDefault()
    if (!item || !comment.trim()) return
    setCommenting(true)
    try {
      await api.addComment(item.id, comment.trim())
      setComment('')
      notify('Comment added to the activity trail.')
      reload()
      setTab('activity')
    } catch (requestError) {
      notify(getErrorMessage(requestError, 'Comment could not be added.'), 'danger')
    } finally {
      setCommenting(false)
    }
  }

  const addEvidence = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file || !item) return
    setUploading(true)
    setUploadMessage(`Uploading ${file.name}…`)
    try {
      await api.addEvidence(item.id, file)
      notify(`${file.name} was securely uploaded and hashed.`)
      reload()
      setTab('evidence')
    } catch (requestError) {
      notify(getErrorMessage(requestError, 'Evidence could not be uploaded.'), 'danger')
    } finally {
      setUploading(false)
      setUploadMessage('')
    }
  }

  const verify = async () => {
    if (!item) return
    setVerifying(true)
    setIntegrity(null)
    try {
      try {
        const job = await api.queueAuditVerification(item.id)
        storeVerificationJob(job, isTerminalVerification(job))
        if (isPendingVerification(job)) {
          notify('Verification queued. Results will update here automatically.')
        }
      } catch (requestError) {
        if (!isServiceUnavailable(requestError)) throw requestError

        const result = await api.verifyAudit(item.id)
        setVerificationJob(null)
        setIntegrity(result)
        notify(
          result.valid ? `Integrity verified across ${result.checkedEvents} events.` : 'A break was detected in the activity chain.',
          result.valid ? 'success' : 'danger',
        )
      }
    } catch (requestError) {
      notify(getErrorMessage(requestError, 'Verification could not be completed.'), 'danger')
    } finally {
      setVerifying(false)
    }
  }

  return (
    <div className="drawer-layer">
      <button className="drawer-scrim" onClick={onClose} aria-label="Close case details" />
      <aside className="case-drawer" role="dialog" aria-modal="true" aria-labelledby="case-detail-title">
        <div className="drawer-top">
          <div><span className="drawer-reference">{item?.reference ?? 'CASE RECORD'}</span><h2 id="case-detail-title">{loading ? 'Loading case…' : item?.title ?? 'Case unavailable'}</h2></div>
          <button ref={closeButton} className="icon-button" onClick={onClose} aria-label="Close case details"><Icon name="close" /></button>
        </div>
        {loading ? <DetailSkeleton /> : error || !item ? <ErrorState message={error || 'Case not found.'} onRetry={() => setReloadKey((key) => key + 1)} /> : <>
          <div className="drawer-summary"><div><StatusBadge value={item.status} /><SeverityBadge value={item.severity} /></div><span>Updated {formatRelative(item.updatedAt)}</span></div>
          <div className="detail-tabs" role="tablist" aria-label="Case details">
            {([
              { id: 'overview', label: 'Overview' },
              { id: 'activity', label: `Activity (${item.activity.length})` },
              { id: 'evidence', label: `Evidence (${item.evidence.length})` },
            ] as { id: DetailTab; label: string }[]).map((entry) => <button role="tab" aria-selected={tab === entry.id} className={tab === entry.id ? 'active' : ''} onClick={() => setTab(entry.id)} key={entry.id}>{entry.label}</button>)}
          </div>
          <div className="drawer-content">
            {tab === 'overview' && <OverviewSection item={item} status={status} setStatus={setStatus} saveStatus={saveStatus} savingStatus={savingStatus} verify={verify} verifying={verifying || isPendingVerification(verificationJob)} integrity={integrity} verificationJob={verificationJob} />}
            {tab === 'activity' && <ActivitySection item={item} comment={comment} setComment={setComment} addComment={addComment} commenting={commenting} />}
            {tab === 'evidence' && <EvidenceSection item={item} addEvidence={addEvidence} uploading={uploading} uploadMessage={uploadMessage} />}
          </div>
        </>}
      </aside>
    </div>
  )
}

function isPendingVerification(job: AuditVerificationJob | null) {
  if (!job) return false
  const status = job.status.toLowerCase()
  return status === 'queued' || status === 'processing'
}

function isTerminalVerification(job: AuditVerificationJob) {
  return !isPendingVerification(job)
}

function DetailSkeleton() {
  return <div className="detail-loading"><span className="skeleton skeleton--pill" /><span className="skeleton skeleton--heading" /><span className="skeleton skeleton--block" />{Array.from({ length: 3 }).map((_, index) => <span key={index} className="skeleton skeleton--row" />)}</div>
}
