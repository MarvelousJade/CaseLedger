import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'

const caseId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
const version = '11111111-1111-1111-1111-111111111111'

function jsonResponse(payload: unknown) {
  return new Response(JSON.stringify(payload), {
    headers: { 'Content-Type': 'application/json' },
  })
}

function casePayload(overrides: Record<string, unknown> = {}) {
  return {
    id: caseId,
    version,
    reference: 'CL-2026-0042',
    title: 'Review access-control policy anomaly',
    summary: 'Review an unexpected permission grant.',
    status: 'New',
    severity: 'Critical',
    category: 'Access Control',
    createdByName: 'Morgan Chen',
    createdAt: '2026-07-15T12:00:00.000Z',
    updatedAt: '2026-07-16T12:00:00.000Z',
    tags: [],
    evidence: [],
    activity: [],
    ...overrides,
  }
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('case API contract', () => {
  it('sends page parameters and preserves pagination metadata', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({
      items: [casePayload()],
      total: 21,
      page: 2,
      pageSize: 10,
      totalPages: 3,
      hasNextPage: true,
      hasPreviousPage: true,
    }))

    const result = await api.getCases({ search: 'policy review', status: 'New', page: 2, pageSize: 10 })

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/cases?search=policy+review&status=New&page=2&pageSize=10',
      expect.objectContaining({ credentials: 'include' }),
    )
    expect(result).toMatchObject({
      total: 21,
      page: 2,
      pageSize: 10,
      totalPages: 3,
      hasNextPage: true,
      hasPreviousPage: true,
    })
    expect(result.items[0]).toMatchObject({ id: caseId, version })
  })

  it('sends the current case version as a strong If-Match ETag', async () => {
    const nextVersion = '22222222-2222-2222-2222-222222222222'
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse(casePayload({
      version: nextVersion,
      status: 'Resolved',
    })))

    const result = await api.updateCase(caseId, version, { status: 'Resolved' })

    expect(fetchMock).toHaveBeenCalledOnce()
    const [path, init] = fetchMock.mock.calls[0]
    const headers = new Headers(init?.headers)
    expect(path).toBe(`/api/cases/${caseId}`)
    expect(init).toMatchObject({ method: 'PATCH', body: JSON.stringify({ status: 'Resolved' }) })
    expect(headers.get('If-Match')).toBe(`"${version}"`)
    expect(headers.get('Content-Type')).toBe('application/json')
    expect(result.version).toBe(nextVersion)
  })
})
