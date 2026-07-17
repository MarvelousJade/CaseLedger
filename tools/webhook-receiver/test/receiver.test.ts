import { once } from 'node:events'
import { request } from 'node:http'
import { afterEach, describe, it } from 'node:test'
import assert from 'node:assert/strict'
import type { AddressInfo } from 'node:net'
import { createWebhookReceiver } from '../src/server.ts'
import { createSignature, timestampIsFresh, verifySignature } from '../src/signature.ts'

const secret = 'local-webhook-test-secret'
const timestamp = '1784232000'
const now = Date.parse('2026-07-16T20:00:00.000Z')
const runningServers: ReturnType<typeof createWebhookReceiver>['server'][] = []

afterEach(async () => {
  await Promise.all(runningServers.splice(0).map(async (server) => {
    server.close()
    await once(server, 'close')
  }))
})

describe('webhook signatures', () => {
  it('verifies the raw body and rejects changed content', () => {
    const body = Buffer.from('{"status":"Completed"}', 'utf8')
    const signature = createSignature(secret, timestamp, body)

    assert.equal(verifySignature(secret, timestamp, body, signature), true)
    assert.equal(
      verifySignature(secret, timestamp, Buffer.from('{"status":"Failed"}'), signature),
      false,
    )
    assert.equal(timestampIsFresh(timestamp, now, 300), true)
    assert.equal(timestampIsFresh('1784231600', now, 300), false)
  })
})

describe('webhook receiver', () => {
  it('accepts a signed delivery and treats an exact replay as a duplicate', async () => {
    const body = Buffer.from('{"jobId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}', 'utf8')
    const receiver = createWebhookReceiver({ secret, now: () => now })
    runningServers.push(receiver.server)
    receiver.server.listen(0, '127.0.0.1')
    await once(receiver.server, 'listening')
    const port = (receiver.server.address() as AddressInfo).port
    const headers = signedHeaders('delivery-1', 'audit.verification.completed.v1', body)

    const first = await send(port, body, headers)
    const duplicate = await send(port, body, headers)

    assert.equal(first.statusCode, 202)
    assert.equal(duplicate.statusCode, 200)
    assert.equal(receiver.deliveries.size, 1)
  })

  it('rejects invalid signatures and conflicting idempotency keys', async () => {
    const firstBody = Buffer.from('{"valid":true}', 'utf8')
    const changedBody = Buffer.from('{"valid":false}', 'utf8')
    const receiver = createWebhookReceiver({ secret, now: () => now })
    runningServers.push(receiver.server)
    receiver.server.listen(0, '127.0.0.1')
    await once(receiver.server, 'listening')
    const port = (receiver.server.address() as AddressInfo).port

    const invalid = await send(port, firstBody, {
      ...signedHeaders('delivery-2', 'audit.verification.completed.v1', firstBody),
      'x-caseledger-signature': `v1=${'0'.repeat(64)}`,
    })
    const accepted = await send(
      port,
      firstBody,
      signedHeaders('delivery-2', 'audit.verification.completed.v1', firstBody),
    )
    const conflict = await send(
      port,
      changedBody,
      signedHeaders('delivery-2', 'audit.verification.completed.v1', changedBody),
    )

    assert.equal(invalid.statusCode, 401)
    assert.equal(accepted.statusCode, 202)
    assert.equal(conflict.statusCode, 409)
  })
})

function signedHeaders(deliveryId: string, eventType: string, body: Buffer) {
  return {
    'content-type': 'application/json',
    'x-caseledger-delivery': deliveryId,
    'x-caseledger-event': eventType,
    'x-caseledger-timestamp': timestamp,
    'x-caseledger-signature': createSignature(secret, timestamp, body),
    'idempotency-key': deliveryId,
  }
}

async function send(port: number, body: Buffer, headers: Record<string, string>) {
  return new Promise<{ statusCode: number; body: string }>((resolve, reject) => {
    const outgoing = request({
      host: '127.0.0.1',
      port,
      path: '/webhooks/caseledger',
      method: 'POST',
      headers: { ...headers, 'content-length': body.byteLength },
    }, (response) => {
      const chunks: Buffer[] = []
      response.on('data', (chunk) => chunks.push(Buffer.from(chunk)))
      response.on('end', () => resolve({
        statusCode: response.statusCode ?? 0,
        body: Buffer.concat(chunks).toString('utf8'),
      }))
    })
    outgoing.on('error', reject)
    outgoing.end(body)
  })
}
