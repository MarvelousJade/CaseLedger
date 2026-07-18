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
  it('reads the public authentication capabilities', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({
      demoLoginEnabled: false,
      entraEnabled: true,
      showDemoCredentials: false,
    }))

    const capabilities = await api.authCapabilities()

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/auth/capabilities',
      expect.objectContaining({ credentials: 'include' }),
    )
    expect(capabilities).toEqual({
      demoLoginEnabled: false,
      entraEnabled: true,
      showDemoCredentials: false,
    })
  })

  it('acquires and sends an antiforgery token when signing in', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ token: 'login-token' }))
      .mockResolvedValueOnce(jsonResponse({
        id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
        name: 'Avery Singh',
        email: 'analyst@caseledger.dev',
        role: 'Analyst',
      }))

    const user = await api.login('analyst@caseledger.dev', 'Analyst123!')

    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(fetchMock.mock.calls[0][0]).toBe('/api/auth/antiforgery')
    const [path, init] = fetchMock.mock.calls[1]
    expect(path).toBe('/api/auth/login')
    expect(init).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(new Headers(init?.headers).get('X-CSRF-TOKEN')).toBe('login-token')
    expect(user).toMatchObject({ email: 'analyst@caseledger.dev', role: 'Analyst' })
  })

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

  it('normalises the redacted administrator failure collection', async () => {
    const failureId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    const deadLetteredAt = '2026-07-18T03:00:00.123456Z'
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({
      items: [{
        kind: 'verification-request',
        id: failureId,
        caseId,
        caseReference: 'CL-2026-0042',
        verificationJobId: failureId,
        occurredAt: '2026-07-18T02:59:00Z',
        deadLetteredAt,
        attemptCount: 8,
        errorCode: 'BROKER_UNAVAILABLE',
        replayable: true,
        replayBlockedReason: null,
      }],
      total: '26',
      page: 2,
      pageSize: 25,
      totalPages: 2,
    }))

    const result = await api.getOperationalFailures({ kind: 'verification-request', page: 2, pageSize: 25 })

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/admin/operations/failures?kind=verification-request&page=2&pageSize=25',
      expect.objectContaining({ credentials: 'include' }),
    )
    expect(result).toMatchObject({ total: 26, page: 2, pageSize: 25, totalPages: 2 })
    expect(result.items[0]).toEqual({
      kind: 'verification-request',
      id: failureId,
      caseId,
      caseReference: 'CL-2026-0042',
      verificationJobId: failureId,
      occurredAt: '2026-07-18T02:59:00Z',
      deadLetteredAt,
      attemptCount: 8,
      errorCode: 'BROKER_UNAVAILABLE',
      replayable: true,
      replayBlockedReason: undefined,
    })
  })

  it('sends guarded verification-request and webhook replay commands', async () => {
    const failureId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    const webhookId = 'cccccccc-cccc-cccc-cccc-cccccccccccc'
    const deadLetteredAt = '2026-07-18T03:00:00.123456Z'
    const fetchMock = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ token: 'request-replay-token' }))
      .mockResolvedValueOnce(jsonResponse({
        replayId: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
        kind: 'verification-request',
        sourceId: failureId,
        sourceDeadLetteredAt: deadLetteredAt,
        replayedAt: '2026-07-18T03:10:00Z',
      }))
      .mockResolvedValueOnce(jsonResponse({ token: 'webhook-replay-token' }))
      .mockResolvedValueOnce(jsonResponse({
        replayId: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
        kind: 'webhook-delivery',
        sourceId: webhookId,
        sourceDeadLetteredAt: deadLetteredAt,
        replayedAt: '2026-07-18T03:11:00Z',
      }))

    const requestInput = { deadLetteredAt, reason: 'Service Bus connectivity restored' }
    const webhookInput = { deadLetteredAt, reason: 'Receiver deployment repaired' }
    const requestReplay = await api.replayVerificationRequest(failureId, requestInput)
    const webhookReplay = await api.replayWebhook(webhookId, webhookInput)

    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual([
      '/api/auth/antiforgery',
      `/api/admin/operations/verification-requests/${failureId}/replay`,
      '/api/auth/antiforgery',
      `/api/admin/operations/webhooks/${webhookId}/replay`,
    ])
    expect(fetchMock.mock.calls[1][1]).toMatchObject({ method: 'POST', body: JSON.stringify(requestInput) })
    expect(new Headers(fetchMock.mock.calls[1][1]?.headers).get('X-CSRF-TOKEN')).toBe('request-replay-token')
    expect(fetchMock.mock.calls[3][1]).toMatchObject({ method: 'POST', body: JSON.stringify(webhookInput) })
    expect(new Headers(fetchMock.mock.calls[3][1]?.headers).get('X-CSRF-TOKEN')).toBe('webhook-replay-token')
    expect(requestReplay).toMatchObject({ kind: 'verification-request', sourceId: failureId, sourceDeadLetteredAt: deadLetteredAt })
    expect(webhookReplay).toMatchObject({ kind: 'webhook-delivery', sourceId: webhookId })
  })

  it('sends the current case version as a strong If-Match ETag', async () => {
    const nextVersion = '22222222-2222-2222-2222-222222222222'
    const fetchMock = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ token: 'update-token' }))
      .mockResolvedValueOnce(jsonResponse(casePayload({
        version: nextVersion,
        status: 'Resolved',
      })))

    const result = await api.updateCase(caseId, version, { status: 'Resolved' })

    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(fetchMock.mock.calls[0]).toEqual([
      '/api/auth/antiforgery',
      expect.objectContaining({ method: 'GET', credentials: 'include', cache: 'no-store' }),
    ])
    const [path, init] = fetchMock.mock.calls[1]
    const headers = new Headers(init?.headers)
    expect(path).toBe(`/api/cases/${caseId}`)
    expect(init).toMatchObject({ method: 'PATCH', body: JSON.stringify({ status: 'Resolved' }) })
    expect(headers.get('If-Match')).toBe(`"${version}"`)
    expect(headers.get('Content-Type')).toBe('application/json')
    expect(headers.get('X-CSRF-TOKEN')).toBe('update-token')
    expect(result.version).toBe(nextVersion)
  })

  it('uploads evidence bytes as multipart data without overriding its boundary', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ token: 'upload-token' }))
      .mockResolvedValueOnce(jsonResponse({
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

    expect(fetchMock).toHaveBeenCalledTimes(2)
    const [path, init] = fetchMock.mock.calls[1]
    const headers = new Headers(init?.headers)
    const body = init?.body as FormData
    expect(path).toBe(`/api/cases/${caseId}/evidence`)
    expect(init).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(headers.has('Content-Type')).toBe(false)
    expect(headers.get('X-CSRF-TOKEN')).toBe('upload-token')
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
      .mockResolvedValueOnce(jsonResponse({ token: 'queue-token' }))
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
      '/api/auth/antiforgery',
      `/api/cases/${caseId}/audit/verifications`,
      `/api/cases/${caseId}/audit/verifications/${jobId}`,
      `/api/cases/${caseId}/audit/verifications/latest`,
    ])
    expect(fetchMock.mock.calls[1][1]).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(new Headers(fetchMock.mock.calls[1][1]?.headers).get('X-CSRF-TOKEN')).toBe('queue-token')
    expect(queued).toMatchObject({ id: jobId, status: 'Queued', isCurrent: true })
    expect(completed).toMatchObject({ valid: true, checkedEvents: 4, brokenAt: undefined })
    expect(latest.isCurrent).toBe(false)
  })

  it('shares one antiforgery-token request across concurrent unsafe calls', async () => {
    let resolveToken!: (response: Response) => void
    const pendingToken = new Promise<Response>((resolve) => { resolveToken = resolve })
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input) => {
      if (String(input) === '/api/auth/antiforgery') return pendingToken
      return Promise.resolve(jsonResponse({ accepted: true }))
    })

    const first = api.addComment('case-one', 'First recovery note')
    const second = api.addComment('case-two', 'Second recovery note')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    resolveToken(jsonResponse({ token: 'shared-token' }))
    await Promise.all([first, second])

    const tokenCalls = fetchMock.mock.calls.filter(([path]) => path === '/api/auth/antiforgery')
    const unsafeCalls = fetchMock.mock.calls.filter(([path]) => String(path).includes('/comments'))
    expect(tokenCalls).toHaveLength(1)
    expect(unsafeCalls).toHaveLength(2)
    expect(unsafeCalls.every(([, init]) => new Headers(init?.headers).get('X-CSRF-TOKEN') === 'shared-token')).toBe(true)
  })

  it('clears failed antiforgery single-flight state so a later unsafe call can recover', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(new Response('unavailable', { status: 503 }))
      .mockResolvedValueOnce(jsonResponse({ token: 'recovery-token' }))
      .mockResolvedValueOnce(jsonResponse({ accepted: true }))

    await expect(api.addComment('case-one', 'This call fails securely')).rejects.toThrow('Unable to secure this request')
    await expect(api.addComment('case-one', 'This call recovers')).resolves.toEqual({ accepted: true })

    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual([
      '/api/auth/antiforgery',
      '/api/auth/antiforgery',
      '/api/cases/case-one/comments',
    ])
    expect(new Headers(fetchMock.mock.calls[2][1]?.headers).get('X-CSRF-TOKEN')).toBe('recovery-token')
  })
})
