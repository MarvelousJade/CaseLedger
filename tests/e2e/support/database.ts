import { runCompose } from './compose'

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

export function tamperWithFirstAuditEvent(caseId: string) {
  assertUuid(caseId, 'case')

  const sql = [
    'UPDATE "AuditEvents"',
    'SET "Description" = "Description" || \' [tampered by E2E test]\'',
    `WHERE "CaseId" = '${caseId}'::uuid`,
    'AND "Sequence" = 1;',
  ].join(' ')

  executeUpdate(sql)
}

export function replayApiVerificationRequest(jobId: string) {
  assertUuid(jobId, 'verification job')
  executeUpdate([
    'UPDATE "OutboxMessages"',
    'SET "PublishedAt" = NULL, "DeadLetteredAt" = NULL,',
    '"NextAttemptAt" = now(), "LockId" = NULL, "LockedUntil" = NULL',
    `WHERE "Id" = '${jobId}'::uuid;`,
  ].join(' '))
}

export function replayWorkerVerificationResult(jobId: string) {
  assertUuid(jobId, 'verification job')
  executeUpdate([
    'UPDATE audit_worker.result_outbox',
    'SET published_at = NULL, next_attempt_at = now(),',
    'lock_id = NULL, locked_until = NULL',
    `WHERE job_id = '${jobId}'::uuid;`,
  ].join(' '))
}

export function apiOutboxAttemptCount(jobId: string) {
  assertUuid(jobId, 'verification job')
  return queryInteger(
    `SELECT "AttemptCount" FROM "OutboxMessages" WHERE "Id" = '${jobId}'::uuid;`,
  )
}

export function workerInboxCount(jobId: string) {
  assertUuid(jobId, 'verification job')
  return queryInteger(
    `SELECT count(*) FROM audit_worker.inbox WHERE job_id = '${jobId}'::uuid;`,
  )
}

export function workerResultPublishAttempts(jobId: string) {
  assertUuid(jobId, 'verification job')
  return queryInteger(
    `SELECT publish_attempts FROM audit_worker.result_outbox WHERE job_id = '${jobId}'::uuid;`,
  )
}

function queryInteger(sql: string) {
  const output = runPsql(sql).trim()
  if (!/^\d+$/.test(output)) {
    throw new Error(`Expected one integer from PostgreSQL, but received: ${output || '<empty>'}`)
  }
  return Number(output)
}

function executeUpdate(sql: string) {
  const output = runPsql(sql)
  if (!/UPDATE 1\b/.test(output)) {
    throw new Error(`Expected to update one row, but psql reported: ${output.trim()}`)
  }
}

function runPsql(sql: string) {
  return runCompose([
    'exec',
    '--no-TTY',
    'database',
    'psql',
    '--username',
    'caseledger',
    '--dbname',
    'caseledger',
    '--set',
    'ON_ERROR_STOP=1',
    '--tuples-only',
    '--no-align',
    '--command',
    sql,
  ], true)
}

function assertUuid(value: string, label: string) {
  if (!uuidPattern.test(value)) {
    throw new Error(`Refusing to use an invalid ${label} identifier: ${value}`)
  }
}
