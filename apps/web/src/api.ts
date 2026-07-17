import type {
  Activity,
  AuthCapabilities,
  AuditVerificationJob,
  CaseCollection,
  CaseItem,
  CreateCaseInput,
  DashboardData,
  Evidence,
  IntegrityResult,
  User,
} from './types'

class ApiError extends Error {
  status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const response = await fetch(path, {
    ...init,
    headers,
    credentials: 'include',
  })

  const contentType = response.headers.get('content-type') ?? ''
  let payload: unknown = null
  if (contentType.includes('application/json') || contentType.includes('+json')) {
    payload = await response.json().catch(() => null)
  } else {
    payload = await response.text().catch(() => '')
  }

  if (!response.ok) {
    const body = asRecord(payload)
    const message =
      readString(body, ['message', 'detail', 'title', 'error']) ||
      (typeof payload === 'string' && payload) ||
      `Request failed (${response.status})`
    throw new ApiError(message, response.status)
  }

  return payload as T
}

function asRecord(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {}
}

function readString(record: Record<string, unknown>, keys: string[], fallback = '') {
  for (const key of keys) {
    const value = record[key]
    if (typeof value === 'string') return value
    if (typeof value === 'number') return String(value)
  }
  return fallback
}

function readNumber(record: Record<string, unknown>, keys: string[], fallback = 0) {
  for (const key of keys) {
    const value = record[key]
    if (typeof value === 'number' && Number.isFinite(value)) return value
    if (typeof value === 'string' && value.trim() && Number.isFinite(Number(value))) {
      return Number(value)
    }
  }
  return fallback
}

function readBoolean(record: Record<string, unknown>, keys: string[], fallback = false) {
  for (const key of keys) {
    const value = record[key]
    if (typeof value === 'boolean') return value
  }
  return fallback
}

function readDate(record: Record<string, unknown>, keys: string[]) {
  return readString(record, keys, new Date().toISOString())
}

function normaliseUser(value: unknown): User {
  const wrapper = asRecord(value)
  const record = asRecord(wrapper.user ?? wrapper.data ?? value)
  const email = readString(record, ['email'])
  const name = readString(record, ['name', 'displayName', 'fullName'], email.split('@')[0] || 'Analyst')
  return {
    id: readString(record, ['id', 'userId']),
    name,
    email,
    role: readString(record, ['role', 'roleName'], 'Analyst'),
  }
}

function normaliseEvidence(value: unknown): Evidence {
  const record = asRecord(value)
  return {
    id: readString(record, ['id', 'evidenceId']),
    fileName: readString(record, ['fileName', 'name'], 'Untitled evidence'),
    sizeBytes: readNumber(record, ['sizeBytes', 'size']),
    mediaType: readString(record, ['mediaType', 'contentType'], 'application/octet-stream'),
    sha256: readString(record, ['sha256', 'hash']),
    addedByName: readString(record, ['addedByName', 'uploadedByName', 'createdByName'], 'Unknown'),
    createdAt: readDate(record, ['createdAt', 'uploadedAt', 'addedAt']),
  }
}

function normaliseActivity(value: unknown): Activity {
  const record = asRecord(value)
  const actor = asRecord(record.actor ?? record.user)
  return {
    id: readString(record, ['id', 'activityId', 'eventId']),
    eventType: readString(record, ['eventType', 'type', 'action'], 'Updated'),
    description: readString(record, ['description', 'detail', 'body', 'message']),
    actorName:
      readString(record, ['actorName', 'createdByName', 'authorName']) ||
      readString(actor, ['name', 'displayName'], 'System'),
    createdAt: readDate(record, ['createdAt', 'occurredAt', 'timestamp']),
  }
}

function normaliseAuditVerificationJob(value: unknown): AuditVerificationJob {
  const wrapper = asRecord(value)
  const record = asRecord(wrapper.job ?? wrapper.data ?? value)
  const valid = record.valid
  const checkedEvents = record.checkedEvents
  const brokenAt = record.brokenAt

  return {
    id: readString(record, ['id', 'jobId']),
    status: readString(record, ['status'], 'Queued'),
    targetSequence: readNumber(record, ['targetSequence']),
    targetHash: readString(record, ['targetHash']),
    resultId: readString(record, ['resultId']) || undefined,
    valid: typeof valid === 'boolean' ? valid : undefined,
    checkedEvents: checkedEvents === null || checkedEvents === undefined
      ? undefined
      : readNumber(record, ['checkedEvents']),
    brokenAt: brokenAt === null || brokenAt === undefined
      ? undefined
      : readNumber(record, ['brokenAt']),
    chainHead: readString(record, ['chainHead']) || undefined,
    snapshotSha256: readString(record, ['snapshotSha256']),
    errorCode: readString(record, ['errorCode']) || undefined,
    requestedAt: readDate(record, ['requestedAt']),
    completedAt: readString(record, ['completedAt']) || undefined,
    isCurrent: readBoolean(record, ['isCurrent']),
  }
}

export function normaliseCase(value: unknown): CaseItem {
  const wrapper = asRecord(value)
  const record = asRecord(wrapper.case ?? wrapper.item ?? wrapper.data ?? value)
  const assignee = asRecord(record.assignee)
  const creator = asRecord(record.createdBy ?? record.creator)
  const evidence = Array.isArray(record.evidence) ? record.evidence.map(normaliseEvidence) : []
  const rawActivity = record.activity ?? record.activities ?? record.auditTrail ?? record.comments
  const activity = Array.isArray(rawActivity) ? rawActivity.map(normaliseActivity) : []
  const rawTags = record.tags

  return {
    id: readString(record, ['id', 'caseId']),
    version: readString(record, ['version']),
    reference: readString(record, ['reference', 'referenceNumber', 'caseNumber'], 'Pending'),
    title: readString(record, ['title', 'name'], 'Untitled case'),
    summary: readString(record, ['summary', 'description']),
    status: readString(record, ['status'], 'Open'),
    severity: readString(record, ['severity', 'priority'], 'Medium'),
    category: readString(record, ['category', 'type'], 'General'),
    assigneeId: readString(record, ['assigneeId']) || readString(assignee, ['id']) || undefined,
    assigneeName:
      readString(record, ['assigneeName']) ||
      readString(assignee, ['name', 'displayName']) ||
      undefined,
    createdByName:
      readString(record, ['createdByName']) || readString(creator, ['name', 'displayName'], 'Unknown'),
    createdAt: readDate(record, ['createdAt']),
    updatedAt: readDate(record, ['updatedAt', 'modifiedAt', 'createdAt']),
    dueAt: readString(record, ['dueAt', 'dueDate']) || undefined,
    tags: Array.isArray(rawTags) ? rawTags.filter((tag): tag is string => typeof tag === 'string') : [],
    evidence,
    activity,
  }
}

function unwrapCollection(value: unknown): CaseCollection {
  if (Array.isArray(value)) {
    const items = value.map(normaliseCase)
    return {
      items,
      total: items.length,
      page: 1,
      pageSize: items.length || 50,
      totalPages: items.length ? 1 : 0,
      hasNextPage: false,
      hasPreviousPage: false,
    }
  }
  const record = asRecord(value)
  const candidates = [record.items, record.cases, asRecord(record.data).items, asRecord(record.data).cases]
  const list = candidates.find(Array.isArray) as unknown[] | undefined
  const items = (list ?? []).map(normaliseCase)
  const total = readNumber(record, ['total', 'totalCount'], items.length)
  const page = readNumber(record, ['page'], 1)
  const pageSize = readNumber(record, ['pageSize'], items.length || 50)
  const totalPages = readNumber(record, ['totalPages'], total ? Math.ceil(total / pageSize) : 0)
  return {
    items,
    total,
    page,
    pageSize,
    totalPages,
    hasNextPage: readBoolean(record, ['hasNextPage'], page < totalPages),
    hasPreviousPage: readBoolean(record, ['hasPreviousPage'], page > 1),
  }
}

export const api = {
  async authCapabilities(): Promise<AuthCapabilities> {
    const payload = asRecord(await request<unknown>('/api/auth/capabilities'))
    const record = asRecord(payload.capabilities ?? payload.data ?? payload)
    return {
      demoLoginEnabled: readBoolean(record, ['demoLoginEnabled']),
      entraEnabled: readBoolean(record, ['entraEnabled']),
      showDemoCredentials: readBoolean(record, ['showDemoCredentials']),
    }
  },

  async login(email: string, password: string) {
    const payload = await request<unknown>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    })
    return normaliseUser(payload)
  },

  async me() {
    return normaliseUser(await request<unknown>('/api/auth/me'))
  },

  async logout() {
    await request<unknown>('/api/auth/logout', { method: 'POST' })
  },

  async getUsers() {
    const payload = await request<unknown>('/api/users')
    const record = asRecord(payload)
    const values = Array.isArray(payload)
      ? payload
      : Array.isArray(record.items)
        ? record.items
        : Array.isArray(record.users)
          ? record.users
          : []
    return values.map(normaliseUser)
  },

  async getCases(filters: { search?: string; status?: string; severity?: string; page?: number; pageSize?: number } = {}) {
    const params = new URLSearchParams()
    if (filters.search) params.set('search', filters.search)
    if (filters.status) params.set('status', filters.status)
    if (filters.severity) params.set('severity', filters.severity)
    if (filters.page !== undefined) params.set('page', String(filters.page))
    if (filters.pageSize !== undefined) params.set('pageSize', String(filters.pageSize))
    const query = params.size ? `?${params.toString()}` : ''
    return unwrapCollection(await request<unknown>(`/api/cases${query}`))
  },

  async getCase(id: string) {
    return normaliseCase(await request<unknown>(`/api/cases/${encodeURIComponent(id)}`))
  },

  async createCase(input: CreateCaseInput) {
    return normaliseCase(
      await request<unknown>('/api/cases', {
        method: 'POST',
        body: JSON.stringify(input),
      }),
    )
  },

  async updateCase(id: string, version: string, patch: Record<string, unknown>) {
    return normaliseCase(
      await request<unknown>(`/api/cases/${encodeURIComponent(id)}`, {
        method: 'PATCH',
        headers: { 'If-Match': `"${version}"` },
        body: JSON.stringify(patch),
      }),
    )
  },

  async addComment(id: string, body: string) {
    return request<unknown>(`/api/cases/${encodeURIComponent(id)}/comments`, {
      method: 'POST',
      body: JSON.stringify({ body }),
    })
  },

  async addEvidence(id: string, file: File): Promise<Evidence> {
    const body = new FormData()
    body.append('file', file, file.name)
    return normaliseEvidence(await request<unknown>(`/api/cases/${encodeURIComponent(id)}/evidence`, {
      method: 'POST',
      body,
    }))
  },

  async verifyAudit(id: string): Promise<IntegrityResult> {
    const payload = asRecord(
      await request<unknown>(`/api/cases/${encodeURIComponent(id)}/audit/verify`),
    )
    return {
      valid: payload.valid === true,
      checkedEvents: readNumber(payload, ['checkedEvents', 'eventCount', 'checked']),
      brokenAt: readString(payload, ['brokenAt']) || undefined,
    }
  },

  async queueAuditVerification(id: string) {
    return normaliseAuditVerificationJob(
      await request<unknown>(`/api/cases/${encodeURIComponent(id)}/audit/verifications`, {
        method: 'POST',
      }),
    )
  },

  async getAuditVerification(id: string, jobId: string) {
    return normaliseAuditVerificationJob(
      await request<unknown>(
        `/api/cases/${encodeURIComponent(id)}/audit/verifications/${encodeURIComponent(jobId)}`,
      ),
    )
  },

  async getLatestAuditVerification(id: string) {
    return normaliseAuditVerificationJob(
      await request<unknown>(`/api/cases/${encodeURIComponent(id)}/audit/verifications/latest`),
    )
  },

  async getDashboard(): Promise<DashboardData> {
    const dashboardQuery = `
      query CaseLedgerDashboard {
        dashboard {
          totalCases
          openCases
          criticalCases
          resolvedCases
          integrityStatus
          recentCases {
            id
            reference
            title
            status
            severity
            updatedAt
          }
        }
      }
    `

    const payload = asRecord(
      await request<unknown>('/graphql', {
        method: 'POST',
        body: JSON.stringify({ query: dashboardQuery, operationName: 'CaseLedgerDashboard' }),
      }),
    )
    const graphData = asRecord(payload.data ?? payload)
    const dashboard = asRecord(
      graphData.dashboard ?? graphData.dashboardSummary ?? graphData.caseDashboard ?? graphData,
    )
    const recentValue = dashboard.recentCases ?? graphData.recentCases
    const recentCases = Array.isArray(recentValue) ? recentValue.map(normaliseCase) : []

    if (Array.isArray(payload.errors) && Object.keys(dashboard).length === 0) {
      const firstError = asRecord(payload.errors[0])
      throw new ApiError(readString(firstError, ['message'], 'Dashboard query failed'), 400)
    }

    return {
      totalCases: readNumber(dashboard, ['totalCases']),
      openCases: readNumber(dashboard, ['openCases']),
      criticalCases: readNumber(dashboard, ['criticalCases']),
      resolvedCases: readNumber(dashboard, ['resolvedCases']),
      integrityStatus: readString(dashboard, ['integrityStatus'], 'Unknown'),
      recentCases,
    }
  },
}

export function isUnauthorised(error: unknown) {
  return error instanceof ApiError && (error.status === 401 || error.status === 403)
}

export function isPreconditionFailed(error: unknown) {
  return typeof error === 'object' && error !== null && 'status' in error && error.status === 412
}

export function isNotFound(error: unknown) {
  return error instanceof ApiError && error.status === 404
}

export function isServiceUnavailable(error: unknown) {
  return typeof error === 'object' && error !== null && 'status' in error && error.status === 503
}

export function getErrorMessage(error: unknown, fallback = 'Something went wrong. Please try again.') {
  return error instanceof Error && error.message ? error.message : fallback
}
