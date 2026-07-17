import { runCompose } from './compose'

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

export function tamperWithFirstAuditEvent(caseId: string) {
  if (!uuidPattern.test(caseId)) {
    throw new Error(`Refusing to use an invalid case identifier: ${caseId}`)
  }

  const sql = [
    'UPDATE "AuditEvents"',
    'SET "Description" = "Description" || \' [tampered by E2E test]\'',
    `WHERE "CaseId" = '${caseId}'::uuid`,
    'AND "Sequence" = 1;',
  ].join(' ')

  const output = runCompose([
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
    '--command',
    sql,
  ], true)

  if (!/UPDATE 1\b/.test(output)) {
    throw new Error(`Expected to tamper with one audit event, but psql reported: ${output.trim()}`)
  }
}
