import { createHash } from 'node:crypto'
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http'
import { timestampIsFresh, verifySignature } from './signature.ts'

export interface ReceiverOptions {
  secret: string
  maximumBodyBytes?: number
  maximumTimestampAgeSeconds?: number
  now?: () => number
}

export interface ReceivedDelivery {
  deliveryId: string
  eventType: string
  bodySha256: string
  receivedAt: string
}

export function createWebhookReceiver(options: ReceiverOptions) {
  if (options.secret.length < 16) {
    throw new Error('WEBHOOK_SIGNING_SECRET must contain at least 16 characters.')
  }

  const maximumBodyBytes = options.maximumBodyBytes ?? 262_144
  const maximumTimestampAgeSeconds = options.maximumTimestampAgeSeconds ?? 300
  const now = options.now ?? Date.now
  const deliveries = new Map<string, ReceivedDelivery>()

  const server = createServer(async (request, response) => {
    try {
      const url = new URL(request.url ?? '/', 'http://localhost')
      if (request.method === 'GET' && url.pathname === '/health') {
        return writeJson(response, 200, { status: 'healthy' })
      }
      if (request.method === 'GET' && url.pathname === '/deliveries') {
        return writeJson(response, 200, { deliveries: [...deliveries.values()] })
      }
      if (request.method === 'DELETE' && url.pathname === '/deliveries') {
        deliveries.clear()
        return writeJson(response, 200, { status: 'cleared' })
      }
      if (request.method !== 'POST' || url.pathname !== '/webhooks/caseledger') {
        return writeJson(response, 404, { error: 'not_found' })
      }

      const deliveryId = singleHeader(request, 'x-caseledger-delivery')
      const eventType = singleHeader(request, 'x-caseledger-event')
      const timestamp = singleHeader(request, 'x-caseledger-timestamp')
      const signature = singleHeader(request, 'x-caseledger-signature')
      const idempotencyKey = singleHeader(request, 'idempotency-key')
      if (!deliveryId || !eventType || !timestamp || !signature || idempotencyKey !== deliveryId) {
        return writeJson(response, 400, { error: 'required_headers_missing' })
      }
      if (!timestampIsFresh(timestamp, now(), maximumTimestampAgeSeconds)) {
        return writeJson(response, 401, { error: 'timestamp_expired' })
      }

      const body = await readBody(request, maximumBodyBytes)
      if (!verifySignature(options.secret, timestamp, body, signature)) {
        return writeJson(response, 401, { error: 'signature_invalid' })
      }
      try {
        JSON.parse(body.toString('utf8'))
      } catch {
        return writeJson(response, 400, { error: 'json_invalid' })
      }

      const bodySha256 = createHash('sha256').update(body).digest('hex')
      const existing = deliveries.get(deliveryId)
      if (existing) {
        if (existing.bodySha256 !== bodySha256 || existing.eventType !== eventType) {
          return writeJson(response, 409, { error: 'idempotency_conflict' })
        }
        return writeJson(response, 200, { status: 'duplicate', deliveryId })
      }

      deliveries.set(deliveryId, {
        deliveryId,
        eventType,
        bodySha256,
        receivedAt: new Date(now()).toISOString(),
      })
      return writeJson(response, 202, { status: 'accepted', deliveryId })
    } catch (error) {
      if (error instanceof BodyTooLargeError) {
        return writeJson(response, 413, { error: 'body_too_large' })
      }
      return writeJson(response, 500, { error: 'receiver_failure' })
    }
  })

  return { server, deliveries }
}

function singleHeader(request: IncomingMessage, name: string) {
  const value = request.headers[name]
  return typeof value === 'string' && value.length <= 200 ? value : undefined
}

async function readBody(request: IncomingMessage, maximumBytes: number) {
  const chunks: Buffer[] = []
  let totalBytes = 0
  for await (const chunk of request) {
    const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)
    totalBytes += buffer.byteLength
    if (totalBytes > maximumBytes) {
      throw new BodyTooLargeError()
    }
    chunks.push(buffer)
  }
  return Buffer.concat(chunks)
}

function writeJson(response: ServerResponse, statusCode: number, value: unknown) {
  const body = JSON.stringify(value)
  response.writeHead(statusCode, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(body),
    'cache-control': 'no-store',
  })
  response.end(body)
}

class BodyTooLargeError extends Error {}
