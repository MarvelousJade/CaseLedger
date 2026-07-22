import { RestApiError } from './errors.js'
import type {
  ListCasesParameters,
  RestAuditVerification,
  RestCaseCollection,
  RestCaseDetail,
  RestProblemDetails,
  RestUser,
} from './types.js'

export type FetchImplementation = (
  input: string | URL | Request,
  init?: RequestInit,
) => Promise<Response>

export class CaseLedgerRestClient {
  constructor(
    private readonly baseUrl: string,
    private readonly accessToken: string,
    private readonly requestId: string,
    private readonly fetchImplementation: FetchImplementation = fetch,
  ) {}

  async listCases(parameters: ListCasesParameters): Promise<RestCaseCollection> {
    const query = new URLSearchParams({
      page: String(parameters.page),
      pageSize: String(parameters.pageSize),
      sortBy: parameters.sortBy,
      sortDirection: parameters.sortDirection,
    })
    appendOptional(query, 'search', parameters.search)
    appendOptional(query, 'status', parameters.status)
    appendOptional(query, 'severity', parameters.severity)
    return this.request<RestCaseCollection>(`/api/cases?${query.toString()}`)
  }

  getCase(id: string): Promise<RestCaseDetail> {
    return this.request<RestCaseDetail>(`/api/cases/${encodeURIComponent(id)}`)
  }

  listUsers(): Promise<RestUser[]> {
    return this.request<RestUser[]>('/api/users')
  }

  verifyAudit(id: string): Promise<RestAuditVerification> {
    return this.request<RestAuditVerification>(
      `/api/cases/${encodeURIComponent(id)}/audit/verify`,
    )
  }

  updateCase(
    id: string,
    expectedVersion: string,
    changes: { assigneeId?: string | null; status?: string },
  ): Promise<RestCaseDetail> {
    return this.request<RestCaseDetail>(`/api/cases/${encodeURIComponent(id)}`, {
      body: JSON.stringify(changes),
      headers: {
        'content-type': 'application/json',
        'if-match': `"${expectedVersion}"`,
      },
      method: 'PATCH',
    })
  }

  private async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const headers = new Headers(init.headers)
    headers.set('accept', 'application/json')
    headers.set('authorization', `Bearer ${this.accessToken}`)
    headers.set('x-request-id', this.requestId)

    let response: Response
    try {
      response = await this.fetchImplementation(`${this.baseUrl}${path}`, {
        ...init,
        headers,
      })
    } catch {
      throw new RestApiError(503, {
        detail: 'The CaseLedger API could not be reached.',
        status: 503,
        title: 'Upstream unavailable',
      })
    }

    if (!response.ok) {
      throw new RestApiError(response.status, await readProblem(response))
    }

    return await response.json() as T
  }
}

async function readProblem(response: Response): Promise<RestProblemDetails | null> {
  try {
    return await response.json() as RestProblemDetails
  } catch {
    return null
  }
}

function appendOptional(
  query: URLSearchParams,
  name: string,
  value: string | undefined,
): void {
  if (value) {
    query.set(name, value)
  }
}
