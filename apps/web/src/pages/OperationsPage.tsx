import { useEffect, useState, type FormEvent } from 'react'
import { api, getErrorMessage } from '../api'
import { Icon } from '../Icons'
import type {
  OperationalFailure,
  OperationalFailureCollection,
  OperationalFailureKind,
} from '../types'
import { formatDate, titleCase } from '../utils'
import { CaseTableSkeleton, EmptyState, ErrorState, PageHeading } from '../components/Common'

const pageSize = 25

const emptyCollection: OperationalFailureCollection = {
  items: [],
  total: 0,
  page: 1,
  pageSize,
  totalPages: 0,
}

export function OperationsPage() {
  const [kind, setKind] = useState<OperationalFailureKind | ''>('')
  const [page, setPage] = useState(1)
  const [collection, setCollection] = useState(emptyCollection)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [reloadKey, setReloadKey] = useState(0)
  const [activeReplayId, setActiveReplayId] = useState<string | null>(null)
  const [reason, setReason] = useState('')
  const [replaying, setReplaying] = useState(false)
  const [replayError, setReplayError] = useState('')
  const [notice, setNotice] = useState('')

  useEffect(() => {
    let active = true
    setLoading(true)
    setError('')
    api.getOperationalFailures({ kind, page, pageSize })
      .then((result) => {
        if (!active) return
        if (result.totalPages > 0 && page > result.totalPages) {
          setPage(result.totalPages)
          return
        }
        setCollection(result)
      })
      .catch((requestError) => {
        if (active) setError(getErrorMessage(requestError, 'Operational failures could not be loaded.'))
      })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [kind, page, reloadKey])

  const openReplay = (failure: OperationalFailure) => {
    setActiveReplayId(failure.id)
    setReason('')
    setReplayError('')
    setNotice('')
  }

  const closeReplay = () => {
    if (replaying) return
    setActiveReplayId(null)
    setReason('')
    setReplayError('')
  }

  const submitReplay = async (event: FormEvent, failure: OperationalFailure) => {
    event.preventDefault()
    const trimmedReason = reason.trim()
    if (!failure.deadLetteredAt || trimmedReason.length < 3 || trimmedReason.length > 240) return

    setReplaying(true)
    setReplayError('')
    setNotice('')
    try {
      const input = { deadLetteredAt: failure.deadLetteredAt, reason: trimmedReason }
      if (failure.kind === 'verification-request') {
        await api.replayVerificationRequest(failure.id, input)
      } else if (failure.kind === 'webhook-delivery') {
        await api.replayWebhook(failure.id, input)
      } else {
        return
      }
      setActiveReplayId(null)
      setReason('')
      setNotice(`${titleCase(failure.kind)} replay accepted. The failure list has been refreshed.`)
      setReloadKey((key) => key + 1)
    } catch (requestError) {
      setReplayError(getErrorMessage(requestError, 'The replay could not be accepted. Refresh and try again.'))
    } finally {
      setReplaying(false)
    }
  }

  return (
    <div className="page operations-page">
      <PageHeading
        eyebrow="ADMINISTRATION"
        title="Operations"
        description="Inspect redacted delivery failures and safely recover eligible work."
        action={(
          <button
            className="button button--secondary"
            type="button"
            disabled={loading}
            onClick={() => setReloadKey((key) => key + 1)}
          >
            <Icon name="refresh" size={17} /> Refresh
          </button>
        )}
      />

      {notice && <div className="operations-notice" role="status"><Icon name="check" size={18} />{notice}</div>}

      <section className="panel operations-panel" aria-labelledby="operations-failures-heading">
        <div className="panel__header operations-panel__header">
          <div>
            <h2 id="operations-failures-heading">Failures requiring attention</h2>
            <p aria-live="polite">{loading ? 'Loading failures…' : `${collection.total} ${collection.total === 1 ? 'failure' : 'failures'}`}</p>
          </div>
          <label className="select-wrap operations-kind-filter">
            <span className="sr-only">Filter by failure kind</span>
            <select
              value={kind}
              onChange={(event) => {
                setKind(event.target.value as OperationalFailureKind | '')
                setPage(1)
                closeReplay()
              }}
            >
              <option value="">All failure kinds</option>
              <option value="verification-request">Verification requests</option>
              <option value="verification-job">Verification jobs</option>
              <option value="webhook-delivery">Webhook deliveries</option>
            </select>
            <Icon name="chevron-down" size={15} />
          </label>
        </div>

        {loading ? <CaseTableSkeleton /> : error ? (
          <ErrorState message={error} onRetry={() => setReloadKey((key) => key + 1)} compact />
        ) : collection.items.length ? (
          <OperationalFailureList
            failures={collection.items}
            activeReplayId={activeReplayId}
            reason={reason}
            replaying={replaying}
            replayError={replayError}
            onOpenReplay={openReplay}
            onCloseReplay={closeReplay}
            onReasonChange={setReason}
            onSubmitReplay={submitReplay}
          />
        ) : (
          <EmptyState
            compact
            icon="check"
            title={kind ? `No ${titleCase(kind).toLowerCase()} failures` : 'No operational failures'}
            message="There is no failed work requiring operator attention."
          />
        )}

        {!error && collection.totalPages > 1 && (
          <OperationsPagination
            collection={collection}
            loading={loading}
            onPageChange={(nextPage) => {
              closeReplay()
              setPage(nextPage)
            }}
          />
        )}
      </section>
    </div>
  )
}

function OperationalFailureList({
  failures,
  activeReplayId,
  reason,
  replaying,
  replayError,
  onOpenReplay,
  onCloseReplay,
  onReasonChange,
  onSubmitReplay,
}: {
  failures: OperationalFailure[]
  activeReplayId: string | null
  reason: string
  replaying: boolean
  replayError: string
  onOpenReplay: (failure: OperationalFailure) => void
  onCloseReplay: () => void
  onReasonChange: (reason: string) => void
  onSubmitReplay: (event: FormEvent, failure: OperationalFailure) => void
}) {
  return (
    <div className="operations-list" aria-label="Operational failures">
      <div className="operations-list__heading" aria-hidden="true">
        <span>Kind</span><span>Case</span><span>Failed at</span><span>Attempts</span><span>Error</span><span>Recovery</span>
      </div>
      {failures.map((failure) => {
        const terminal = failure.kind === 'verification-job'
        const canReplay = failure.replayable && Boolean(failure.deadLetteredAt) && !terminal
        const replayOpen = activeReplayId === failure.id
        const displayTime = failure.deadLetteredAt ?? failure.occurredAt
        const headingId = `operational-failure-${failure.id}`
        return (
          <article className={`operations-row ${replayOpen ? 'operations-row--expanded' : ''}`} key={`${failure.kind}:${failure.id}`} aria-labelledby={headingId}>
            <div className="operations-cell operations-cell--kind">
              <small>Kind</small>
              <span className={`operations-kind operations-kind--${failure.kind}`} id={headingId}>
                <Icon name={terminal ? 'fingerprint' : failure.kind === 'webhook-delivery' ? 'arrow-right' : 'activity'} size={15} />
                {titleCase(failure.kind)}
              </span>
            </div>
            <div className="operations-cell operations-cell--case">
              <small>Case</small>
              <b>{failure.caseReference ?? 'No linked case'}</b>
              {failure.caseId && <code title={failure.caseId}>{failure.caseId}</code>}
            </div>
            <div className="operations-cell operations-cell--time">
              <small>Failed at</small>
              <time dateTime={displayTime}>{formatDate(displayTime, true)}</time>
              <span>{failure.deadLetteredAt ? 'Dead-lettered' : 'Occurred'}</span>
            </div>
            <div className="operations-cell operations-cell--attempts">
              <small>Attempts</small>
              <b>{failure.attemptCount ?? '—'}</b>
            </div>
            <div className="operations-cell operations-cell--error">
              <small>Error</small>
              {failure.errorCode ? <code title={failure.errorCode}>{failure.errorCode}</code> : <span>Not reported</span>}
            </div>
            <div className="operations-cell operations-cell--recovery">
              <small>Recovery</small>
              {terminal ? (
                <span className="operations-replay-state operations-replay-state--terminal"><Icon name="warning" size={14} /> Terminal · non-replayable</span>
              ) : canReplay ? (
                <span className="operations-replay-state operations-replay-state--ready"><i /> Ready to replay</span>
              ) : (
                <span className="operations-replay-state operations-replay-state--blocked"><Icon name="close" size={13} /> Not replayable</span>
              )}
              {!canReplay && failure.replayBlockedReason && <p>{failure.replayBlockedReason}</p>}
              {canReplay && !replayOpen && (
                <button className="button button--secondary operations-replay-button" type="button" onClick={() => onOpenReplay(failure)}>
                  <Icon name="refresh" size={15} /> Replay
                </button>
              )}
            </div>

            {replayOpen && failure.deadLetteredAt && (
              <form className="operations-replay-form" onSubmit={(event) => onSubmitReplay(event, failure)}>
                <div className="operations-replay-form__intro">
                  <span><Icon name="shield" size={18} /></span>
                  <div><h3>Confirm guarded replay</h3><p>The replay is accepted only if this exact dead-letter observation is still current.</p></div>
                </div>
                <div className="operations-guard">
                  <small>Observed dead-letter time</small>
                  <code>{failure.deadLetteredAt}</code>
                </div>
                <label className="field operations-reason-field">
                  <span>Recovery reason <i>Required</i></span>
                  <input
                    autoFocus
                    type="text"
                    required
                    minLength={3}
                    maxLength={240}
                    placeholder="Briefly describe what was repaired…"
                    value={reason}
                    onChange={(event) => onReasonChange(event.target.value)}
                  />
                  <small>{reason.trim().length}/240 characters · minimum 3</small>
                </label>
                {replayError && <div className="alert alert--danger operations-replay-error" role="alert"><Icon name="warning" size={17} />{replayError}</div>}
                <div className="operations-replay-form__actions">
                  <button className="button button--ghost" type="button" disabled={replaying} onClick={onCloseReplay}>Cancel</button>
                  <button className="button button--primary" type="submit" disabled={replaying || reason.trim().length < 3}>
                    {replaying ? <><span className="button-spinner" /> Replaying…</> : <><Icon name="refresh" size={16} /> Confirm replay</>}
                  </button>
                </div>
              </form>
            )}
          </article>
        )
      })}
    </div>
  )
}

function OperationsPagination({ collection, loading, onPageChange }: {
  collection: OperationalFailureCollection
  loading: boolean
  onPageChange: (page: number) => void
}) {
  const firstItem = (collection.page - 1) * collection.pageSize + 1
  const lastItem = Math.min(firstItem + collection.items.length - 1, collection.total)
  return (
    <nav className="case-pagination" aria-label="Operational failure pages">
      <span className="case-pagination__summary" aria-live="polite">Showing {firstItem}–{lastItem} of {collection.total} failures</span>
      <div className="case-pagination__controls">
        <button className="button button--ghost case-pagination__previous" type="button" disabled={loading || collection.page <= 1} onClick={() => onPageChange(collection.page - 1)}><Icon name="arrow-right" size={15} /> Previous</button>
        <span>Page <b>{collection.page}</b> of {collection.totalPages}</span>
        <button className="button button--ghost" type="button" disabled={loading || collection.page >= collection.totalPages} onClick={() => onPageChange(collection.page + 1)}>Next <Icon name="arrow-right" size={15} /></button>
      </div>
    </nav>
  )
}
