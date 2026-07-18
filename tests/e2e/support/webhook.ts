const webhookPort = process.env.CASELEDGER_E2E_WEBHOOK_PORT ?? '5154'
const receiverUrl =
  process.env.WEBHOOK_RECEIVER_URL ?? `http://127.0.0.1:${webhookPort}`

interface WebhookDeliveryMetadata {
  deliveryId: string
  eventType: string
  bodySha256: string
  receivedAt: string
}

export async function clearWebhookDeliveries() {
  const response = await fetch(`${receiverUrl}/deliveries`, { method: 'DELETE' })
  if (!response.ok) {
    throw new Error(`Webhook receiver reset failed with status ${response.status}.`)
  }
}

export async function webhookDeliveries(): Promise<WebhookDeliveryMetadata[]> {
  const response = await fetch(`${receiverUrl}/deliveries`)
  if (!response.ok) {
    throw new Error(`Webhook receiver query failed with status ${response.status}.`)
  }

  const value = await response.json() as { deliveries?: unknown }
  if (!Array.isArray(value.deliveries)) {
    throw new Error('Webhook receiver returned an invalid delivery collection.')
  }
  return value.deliveries as WebhookDeliveryMetadata[]
}
