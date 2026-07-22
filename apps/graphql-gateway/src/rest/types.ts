export interface RestUser {
  id: string
  name: string
  email: string
  role: string
}

export interface RestCaseSummary {
  id: string
  version: string
  reference: string
  title: string
  summary: string
  status: string
  severity: string
  category: string
  assigneeId: string | null
  assigneeName: string | null
  createdByName: string
  createdAt: string
  updatedAt: string
  dueAt: string | null
  tags: string[]
}

export interface RestEvidence {
  id: string
  fileName: string
  sizeBytes: number
  mediaType: string
  sha256: string
  addedByName: string
  createdAt: string
}

export interface RestCaseEvent {
  id: string
  eventType: string
  description: string
  actorName: string
  createdAt: string
}

export interface RestCaseDetail extends RestCaseSummary {
  evidence: RestEvidence[]
  activity: RestCaseEvent[]
}

export interface RestCaseCollection {
  items: RestCaseSummary[]
  total: number
  page: number
  pageSize: number
  totalPages: number
  hasNextPage: boolean
  hasPreviousPage: boolean
}

export interface RestAuditVerification {
  valid: boolean
  checkedEvents: number
  brokenAt: number | null
}

export interface RestProblemDetails {
  detail?: string
  errors?: Record<string, string[]>
  status?: number
  title?: string
  traceId?: string
}

export interface ListCasesParameters {
  page: number
  pageSize: number
  search?: string | undefined
  severity?: string | undefined
  sortBy: string
  sortDirection: string
  status?: string | undefined
}
