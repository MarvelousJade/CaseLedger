import { createWebhookReceiver } from './server.ts'

const secret = process.env.WEBHOOK_SIGNING_SECRET ?? ''
const host = process.env.HOST || '127.0.0.1'
const port = readPositiveInteger(process.env.PORT, 8082, 65_535)
const maximumBodyBytes = readPositiveInteger(process.env.MAX_BODY_BYTES, 262_144, 10_485_760)
const maximumTimestampAgeSeconds = readPositiveInteger(
  process.env.MAX_TIMESTAMP_AGE_SECONDS,
  300,
  86_400,
)

const { server } = createWebhookReceiver({
  secret,
  maximumBodyBytes,
  maximumTimestampAgeSeconds,
})

server.listen(port, host, () => {
  process.stdout.write(`${JSON.stringify({ event: 'webhook_receiver_started', host, port })}\n`)
})

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.once(signal, () => {
    server.close(() => process.exit(0))
  })
}

function readPositiveInteger(value: string | undefined, fallback: number, maximum: number) {
  if (value === undefined) return fallback
  const parsed = Number(value)
  if (!Number.isSafeInteger(parsed) || parsed < 1 || parsed > maximum) {
    throw new Error('Webhook receiver numeric configuration is invalid.')
  }
  return parsed
}
