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

  it('uploads evidence bytes as multipart data without overriding its boundary', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({
      id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
      fileName: 'server-hash.txt',
      sizeBytes: 14,
      mediaType: 'text/plain',
      sha256: 'a'.repeat(64),
      addedByName: 'Avery Singh',
      createdAt: '2026-07-17T12:00:00.000Z',
    }))
    const file = new File(['evidence bytes'], 'server-hash.txt', { type: 'text/plain' })

    const evidence = await api.addEvidence(caseId, file)

    expect(fetchMock).toHaveBeenCalledOnce()
    const [path, init] = fetchMock.mock.calls[0]
    const headers = new Headers(init?.headers)
    const body = init?.body as FormData
    expect(path).toBe(`/api/cases/${caseId}/evidence`)
    expect(init).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(headers.has('Content-Type')).toBe(false)
    expect(body).toBeInstanceOf(FormData)
    const uploaded = body.get('file')
    expect(uploaded).toBeInstanceOf(File)
    expect((uploaded as File).name).toBe(file.name)
    expect((uploaded as File).type).toBe(file.type)
    expect(await (uploaded as File).text()).toBe('evidence bytes')
    expect(evidence).toMatchObject({
      fileName: 'server-hash.txt',
      sha256: 'a'.repeat(64),
    })
  })

  it('queues and reads durable audit verification jobs', async () => {
    const requestedAt = '2026-07-17T02:00:00.000Z'
    const completedAt = '2026-07-17T02:00:01.000Z'
    const jobId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    const fetchMock = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({
        id: jobId,
        status: 'Queued',
        targetSequence: 4,
        targetHash: 'a'.repeat(64),
        snapshotSha256: 'b'.repeat(64),
        requestedAt,
        isCurrent: true,
      }))
      .mockResolvedValueOnce(jsonResponse({
        id: jobId,
        status: 'Completed',
        targetSequence: 4,
        targetHash: 'a'.repeat(64),
        resultId: `audit-verification:${jobId}:v1`,
        valid: true,
        checkedEvents: 4,
        brokenAt: null,
        chainHead: 'a'.repeat(64),
        snapshotSha256: 'b'.repeat(64),
        errorCode: null,
        requestedAt,
        completedAt,
        isCurrent: true,
      }))
      .mockResolvedValueOnce(jsonResponse({
        id: jobId,
        status: 'Completed',
        targetSequence: 4,
        targetHash: 'a'.repeat(64),
        valid: true,
        checkedEvents: 4,
        snapshotSha256: 'b'.repeat(64),
        requestedAt,
        completedAt,
        isCurrent: false,
      }))

    const queued = await api.queueAuditVerification(caseId)
    const completed = await api.getAuditVerification(caseId, jobId)
    const latest = await api.getLatestAuditVerification(caseId)

    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual([
      `/api/cases/${caseId}/audit/verifications`,
      `/api/cases/${caseId}/audit/verifications/${jobId}`,
      `/api/cases/${caseId}/audit/verifications/latest`,
    ])
    expect(fetchMock.mock.calls[0][1]).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(queued).toMatchObject({ id: jobId, status: 'Queued', isCurrent: true })
    expect(completed).toMatchObject({ valid: true, checkedEvents: 4, brokenAt: undefined })
    expect(latest.isCurrent).toBe(false)
  })
})
