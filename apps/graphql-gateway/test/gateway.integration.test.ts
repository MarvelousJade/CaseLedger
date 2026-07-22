import { SignJWT } from 'jose'
import { beforeEach, describe, expect, it } from 'vitest'
import { createGateway } from '../src/server.js'
import type { GatewayConfig } from '../src/config.js'
import type { FetchImplementation } from '../src/rest/client.js'
import type {
  RestCaseCollection,
  RestCaseDetail,
  RestUser,
} from '../src/rest/types.js'

const signingKey = 'caseledger-gateway-tests-signing-key-do-not-use'
const caseId = '20000000-0000-0000-0000-000000000001'
const secondCaseId = '20000000-0000-0000-0000-000000000002'
const investigatorId = '10000000-0000-0000-0000-000000000002'
const version = '30000000-0000-0000-0000-000000000001'

const config: GatewayConfig = {
  apiBaseUrl: 'http://caseledger-api.test',
  corsOrigins: ['http://caseledger.test'],
  host: '127.0.0.1',
  isProduction: false,
  jwtAudience: 'CaseLedger.GraphQL',
  jwtIssuer: 'CaseLedger.Api',
  jwtSigningKey: signingKey,
  port: 5155,
}

describe('GraphQL gateway integration', () => {
  let backend: MockCaseLedgerApi

  beforeEach(() => {
    backend = new MockCaseLedgerApi()
  })

  it('exposes an anonymous health probe without calling the REST API', async () => {
    const yoga = createGateway({ config, fetchImplementation: backend.fetch })

    const response = await yoga.fetch('http://gateway.test/health')

    expect(response.status).toBe(200)
    expect(backend.calls).toHaveLength(0)
  })

  it('requires a valid bearer token before executing operations', async () => {
    const response = await execute(
      backend.fetch,
      '{ cases { pageInfo { totalCount } } }',
      undefined,
      null,
    )

    expect(response.errors?.[0]?.extensions?.code).toBe('UNAUTHENTICATED')
    expect(backend.calls).toHaveLength(0)
  })

  it('forwards filters, sorting, pagination, and batches investigators', async () => {
    const response = await execute(
      backend.fetch,
      `
        query Cases($filter: CasesFilter!, $sort: CaseSortInput!, $page: PaginationInput!) {
          cases(filter: $filter, sort: $sort, pagination: $page) {
            nodes {
              id
              status
              severity
              investigator { id name }
            }
            pageInfo { page pageSize totalCount hasNextPage }
          }
        }
      `,
      {
        filter: { search: 'policy', status: 'NEW', severity: 'CRITICAL' },
        page: { page: 2, pageSize: 10 },
        sort: { field: 'TITLE', direction: 'ASC' },
      },
    )

    expect(response.errors).toBeUndefined()
    expect(response.data?.cases.pageInfo).toMatchObject({
      page: 2,
      pageSize: 10,
      totalCount: 2,
    })
    expect(response.data?.cases.nodes).toHaveLength(2)
    expect(response.data?.cases.nodes[0].investigator.name).toBe('Avery Singh')

    const casesCall = backend.calls.find((call) => call.url.pathname === '/api/cases')
    expect(casesCall?.url.searchParams.get('search')).toBe('policy')
    expect(casesCall?.url.searchParams.get('status')).toBe('New')
    expect(casesCall?.url.searchParams.get('severity')).toBe('Critical')
    expect(casesCall?.url.searchParams.get('sortBy')).toBe('title')
    expect(casesCall?.url.searchParams.get('sortDirection')).toBe('asc')
    expect(casesCall?.url.searchParams.get('page')).toBe('2')
    expect(casesCall?.url.searchParams.get('pageSize')).toBe('10')
    expect(backend.count('/api/users')).toBe(1)
    expect(backend.calls.every((call) => call.authorization.startsWith('Bearer '))).toBe(true)
  })

  it('deduplicates case and analytics REST reads within one GraphQL request', async () => {
    const response = await execute(
      backend.fetch,
      `
        query CaseAggregate($id: UUID!) {
          first: case(id: $id) {
            id
            events { eventType actorName }
            analytics {
              eventCount
              evidenceCount
              dueState
              integrity { valid checkedEvents }
            }
          }
          second: case(id: $id) {
            id
            analytics { integrity { valid } }
          }
        }
      `,
      { id: caseId },
    )

    expect(response.errors).toBeUndefined()
    expect(response.data?.first.events).toHaveLength(1)
    expect(response.data?.first.analytics).toMatchObject({
      eventCount: 1,
      evidenceCount: 1,
      integrity: { checkedEvents: 1, valid: true },
    })
    expect(response.data?.second.id).toBe(caseId)
    expect(backend.count(`/api/cases/${caseId}`)).toBe(1)
    expect(backend.count(`/api/cases/${caseId}/audit/verify`)).toBe(1)
  })

  it('updates status with optimistic concurrency and structured conflicts', async () => {
    const successful = await execute(
      backend.fetch,
      `
        mutation Update($input: UpdateCaseStatusInput!) {
          updateCaseStatus(input: $input) { case { id version status } }
        }
      `,
      { input: { expectedVersion: version, id: caseId, status: 'RESOLVED' } },
    )

    expect(successful.errors).toBeUndefined()
    expect(successful.data?.updateCaseStatus.case.status).toBe('RESOLVED')
    const patch = backend.calls.find((call) => call.method === 'PATCH')
    expect(patch?.ifMatch).toBe(`"${version}"`)
    expect(patch?.body).toEqual({ status: 'Resolved' })

    const conflict = await execute(
      backend.fetch,
      `mutation Update($input: UpdateCaseStatusInput!) {
        updateCaseStatus(input: $input) { case { id } }
      }`,
      { input: { expectedVersion: backend.staleVersion, id: caseId, status: 'NEW' } },
    )
    expect(conflict.errors?.[0]?.extensions).toMatchObject({
      code: 'CONFLICT',
      upstream: { status: 412 },
    })
  })

  it('restricts investigator assignment to administrators', async () => {
    const mutation = `
      mutation Assign($input: AssignInvestigatorInput!) {
        assignInvestigator(input: $input) {
          case { id investigator { id } }
        }
      }
    `
    const variables = {
      input: { expectedVersion: version, id: caseId, investigatorId },
    }

    const forbidden = await execute(backend.fetch, mutation, variables)
    expect(forbidden.errors?.[0]?.extensions?.code).toBe('FORBIDDEN')
    expect(backend.calls.filter((call) => call.method === 'PATCH')).toHaveLength(0)

    const accepted = await execute(
      backend.fetch,
      mutation,
      variables,
      await accessToken('Admin'),
    )
    expect(accepted.errors).toBeUndefined()
    expect(accepted.data?.assignInvestigator.case.investigator.id).toBe(investigatorId)
    expect(backend.calls.find((call) => call.method === 'PATCH')?.body).toEqual({
      assigneeId: investigatorId,
    })
  })

  it('rejects invalid pagination and invalid GraphQL selections before REST calls', async () => {
    const invalidPage = await execute(
      backend.fetch,
      'query { cases(pagination: { page: 0, pageSize: 101 }) { nodes { id } } }',
    )
    expect(invalidPage.errors?.[0]?.extensions).toMatchObject({
      code: 'BAD_USER_INPUT',
      field: 'pagination.page',
    })

    const invalidSelection = await execute(
      backend.fetch,
      'query { cases { nodes { fieldThatDoesNotExist } } }',
    )
    expect(invalidSelection.errors?.[0]?.message).toContain('fieldThatDoesNotExist')
    expect(backend.calls).toHaveLength(0)
  })
})

interface GraphQLResponse {
  data?: any
  errors?: Array<{
    extensions?: Record<string, any>
    message: string
  }>
}

async function execute(
  fetchImplementation: FetchImplementation,
  query: string,
  variables?: Record<string, unknown>,
  token: string | null | undefined = undefined,
): Promise<GraphQLResponse> {
  const yoga = createGateway({ config, fetchImplementation })
  const authorization = token === undefined ? await accessToken('Analyst') : token
  const headers = new Headers({ 'content-type': 'application/json' })
  if (authorization) {
    headers.set('authorization', `Bearer ${authorization}`)
  }
  const response = await yoga.fetch('http://gateway.test/graphql', {
    body: JSON.stringify({ query, variables }),
    headers,
    method: 'POST',
  })
  return await response.json() as GraphQLResponse
}

async function accessToken(role: 'Admin' | 'Analyst'): Promise<string> {
  return await new SignJWT({
    email: `${role.toLowerCase()}@caseledger.dev`,
    name: role === 'Admin' ? 'Morgan Chen' : 'Avery Singh',
    role,
  })
    .setProtectedHeader({ alg: 'HS256' })
    .setSubject(role === 'Admin'
      ? '10000000-0000-0000-0000-000000000001'
      : investigatorId)
    .setIssuer(config.jwtIssuer)
    .setAudience(config.jwtAudience)
    .setIssuedAt()
    .setExpirationTime('15m')
    .sign(new TextEncoder().encode(signingKey))
}

interface RecordedCall {
  authorization: string
  body: Record<string, unknown> | null
  ifMatch: string | null
  method: string
  url: URL
}

class MockCaseLedgerApi {
  readonly calls: RecordedCall[] = []
  readonly staleVersion = '30000000-0000-0000-0000-000000000099'
  private readonly users: RestUser[] = [
    {
      email: 'analyst@caseledger.dev',
      id: investigatorId,
      name: 'Avery Singh',
      role: 'Analyst',
    },
  ]
  private readonly cases: RestCaseDetail[] = [
    caseFixture(caseId, 'CL-2026-001'),
    caseFixture(secondCaseId, 'CL-2026-002'),
  ]

  readonly fetch: FetchImplementation = async (input, init = {}) => {
    const url = new URL(input instanceof Request ? input.url : input.toString())
    const headers = new Headers(init.headers)
    const method = init.method ?? 'GET'
    const body = typeof init.body === 'string'
      ? JSON.parse(init.body) as Record<string, unknown>
      : null
    this.calls.push({
      authorization: headers.get('authorization') ?? '',
      body,
      ifMatch: headers.get('if-match'),
      method,
      url,
    })

    if (!headers.get('authorization')?.startsWith('Bearer ')) {
      return json({ title: 'Authentication required' }, 401)
    }
    if (url.pathname === '/api/cases' && method === 'GET') {
      const response: RestCaseCollection = {
        hasNextPage: true,
        hasPreviousPage: true,
        items: this.cases,
        page: Number(url.searchParams.get('page')),
        pageSize: Number(url.searchParams.get('pageSize')),
        total: this.cases.length,
        totalPages: 1,
      }
      return json(response)
    }
    if (url.pathname === '/api/users') {
      return json(this.users)
    }

    const item = this.cases.find((candidate) =>
      url.pathname.startsWith(`/api/cases/${candidate.id}`))
    if (!item) {
      return json({ detail: 'Case not found.', status: 404, title: 'Not found' }, 404)
    }
    if (url.pathname.endsWith('/audit/verify')) {
      return json({ brokenAt: null, checkedEvents: item.activity.length, valid: true })
    }
    if (method === 'PATCH') {
      if (headers.get('if-match') === `"${this.staleVersion}"`) {
        return json({
          detail: 'The case was changed by another request.',
          status: 412,
          title: 'Precondition failed',
          traceId: 'rest-trace-123',
        }, 412)
      }
      const updated: RestCaseDetail = {
        ...item,
        assigneeId: body?.assigneeId === null
          ? null
          : typeof body?.assigneeId === 'string'
            ? body.assigneeId
            : item.assigneeId,
        status: typeof body?.status === 'string' ? body.status : item.status,
        version: '30000000-0000-0000-0000-000000000002',
      }
      return json(updated)
    }
    return json(item)
  }

  count(path: string): number {
    return this.calls.filter((call) => call.url.pathname === path).length
  }
}

function caseFixture(id: string, reference: string): RestCaseDetail {
  return {
    activity: [{
      actorName: 'Avery Singh',
      createdAt: '2026-07-15T12:00:00Z',
      description: 'Case created',
      eventType: 'CaseCreated',
      id: `40000000-0000-0000-0000-${id.slice(-12)}`,
    }],
    assigneeId: investigatorId,
    assigneeName: 'Avery Singh',
    category: 'Security Operations',
    createdAt: '2026-07-15T12:00:00Z',
    createdByName: 'Morgan Chen',
    dueAt: '2026-07-30T12:00:00Z',
    evidence: [{
      addedByName: 'Avery Singh',
      createdAt: '2026-07-15T13:00:00Z',
      fileName: 'evidence.json',
      id: `50000000-0000-0000-0000-${id.slice(-12)}`,
      mediaType: 'application/json',
      sha256: 'a'.repeat(64),
      sizeBytes: 42,
    }],
    id,
    reference,
    severity: 'Critical',
    status: 'New',
    summary: 'A focused integration-test case summary.',
    tags: ['gateway'],
    title: 'Review access policy anomaly',
    updatedAt: '2026-07-16T12:00:00Z',
    version,
  }
}

function json(value: unknown, status = 200): Response {
  return Response.json(value, { status })
}
