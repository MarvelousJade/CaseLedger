import { createHmac, timingSafeEqual } from 'node:crypto'

const signaturePattern = /^v1=([0-9a-f]{64})$/

export function createSignature(secret: string, timestamp: string, body: Buffer) {
  return `v1=${createHmac('sha256', secret)
    .update(timestamp, 'utf8')
    .update('.', 'utf8')
    .update(body)
    .digest('hex')}`
}

export function verifySignature(
  secret: string,
  timestamp: string,
  body: Buffer,
  providedSignature: string,
) {
  const match = signaturePattern.exec(providedSignature)
  if (!match) return false

  const expected = Buffer.from(createSignature(secret, timestamp, body).slice(3), 'hex')
  const provided = Buffer.from(match[1], 'hex')
  return expected.length === provided.length && timingSafeEqual(expected, provided)
}

export function timestampIsFresh(
  timestamp: string,
  nowMilliseconds: number,
  maximumAgeSeconds: number,
) {
  if (!/^\d{1,12}$/.test(timestamp)) return false
  const timestampSeconds = Number(timestamp)
  if (!Number.isSafeInteger(timestampSeconds)) return false

  const difference = Math.abs(Math.floor(nowMilliseconds / 1_000) - timestampSeconds)
  return difference <= maximumAgeSeconds
}
