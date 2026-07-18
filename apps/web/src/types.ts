export type CaseStatus = 'New' | 'InProgress' | 'Resolved' | string

export type CaseSeverity = 'Low' | 'Medium' | 'High' | 'Critical' | string

export interface User {
  id: string
  name: string
  email: string
  role: string
}

export interface AuthCapabilities {
  demoLoginEnabled: boolean
  entraEnabled: boolean
  showDemoCredentials: boolean
}

export interface Evidence {
  id: string
  fileName: string
  sizeBytes: number
  mediaType: string
  sha256: string
  addedByName: string
  createdAt: string
}

export interface Activity {
  id: string
  eventType: string
  description: string
  actorName: string
  createdAt: string
}

export interface CaseItem {
  id: string
  version: string
  reference: string
  title: string
  summary: string
  status: CaseStatus
  severity: CaseSeverity
  category: string
  assigneeId?: string
  assigneeName?: string
  createdByName: string
  createdAt: string
  updatedAt: string
  dueAt?: string
  tags: string[]
  evidence: Evidence[]
  activity: Activity[]
}

export interface CaseCollection {
  items: CaseItem[]
  total: number
  page: number
  pageSize: number
  totalPages: number
  hasNextPage: boolean
  hasPreviousPage: boolean
}

export interface DashboardData {
  totalCases: number
  openCases: number
  criticalCases: number
  resolvedCases: number
  integrityStatus: string
  recentCases: CaseItem[]
}

export interface IntegrityResult {
  valid: boolean
  checkedEvents: number
  brokenAt?: string
}

export interface AuditVerificationJob {
  id: string
  status: string
  targetSequence: number
  targetHash: string
  resultId?: string
  valid?: boolean
  checkedEvents?: number
  brokenAt?: number
  chainHead?: string
  snapshotSha256: string
  errorCode?: string
  requestedAt: string
  completedAt?: string
  isCurrent: boolean
}

export type OperationalFailureKind =
  | 'verification-request'
  | 'verification-job'
  | 'webhook-delivery'

export interface OperationalFailure {
  kind: OperationalFailureKind
  id: string
  caseId?: string
  caseReference?: string
  verificationJobId?: string
  occurredAt: string
  deadLetteredAt?: string
  attemptCount?: number
  errorCode?: string
  replayable: boolean
  replayBlockedReason?: string
}

export interface OperationalFailureCollection {
  items: OperationalFailure[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export interface OperationalReplayInput {
  deadLetteredAt: string
  reason: string
}

export interface OperationalReplay {
  replayId: string
  kind: OperationalFailureKind
  sourceId: string
  sourceDeadLetteredAt: string
  replayedAt: string
}

export interface VerificationUpdated {
  jobId: string
  caseId: string
  resultId: string
  status: string
  valid?: boolean
  checkedEvents?: number
  brokenAt?: number
  completedAt?: string
}

export interface CreateCaseInput {
  title: string
  summary: string
  severity: CaseSeverity
  category: string
  assigneeId?: string
  dueAt?: string
  tags: string[]
}
