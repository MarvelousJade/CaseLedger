import { connect, type ChannelModel, type ConfirmChannel } from 'amqplib'
import { setTimeout as delay } from 'node:timers/promises'

const brokerUrl = 'amqp://caseledger:caseledger-e2e@127.0.0.1:5673/'
const managementUrl = 'http://127.0.0.1:15673'
const managementAuthorization = `Basic ${Buffer.from('caseledger:caseledger-e2e').toString('base64')}`
const auditExchange = 'caseledger.audit'
const requestRoutingKey = 'audit.verification.requested.v1'
const requestQueue = 'caseledger.audit.verify.v1'
const resultQueue = 'caseledger.api.audit-verification-results.v1'
const requestDeadLetterQueue = 'caseledger.audit.verify.dead.v1'

export interface PublishedTestMessage {
  messageId: string
  correlationId: string
  body: Buffer
}

export async function publishInvalidVerificationRequest(message: PublishedTestMessage) {
  await withConfirmChannel(async (channel) => {
    channel.publish(auditExchange, requestRoutingKey, message.body, {
      appId: 'caseledger-e2e',
      contentType: 'application/json',
      contentEncoding: 'utf-8',
      persistent: true,
      messageId: message.messageId,
      correlationId: message.correlationId,
      type: 'caseledger.audit.verification.requested.v1',
      headers: { 'x-caseledger-schema-version': 1 },
    })
    await channel.waitForConfirms()
  })
}

export async function waitForDeadLetter(timeoutMilliseconds = 10_000) {
  const deadline = Date.now() + timeoutMilliseconds
  while (Date.now() < deadline) {
    const message = await readDeadLetter()
    if (message) return message
    await delay(100)
  }
  throw new Error('A worker dead-letter message was not available before the deadline.')
}

export async function waitForVerificationQueuesToDrain(timeoutMilliseconds = 10_000) {
  const deadline = Date.now() + timeoutMilliseconds
  while (Date.now() < deadline) {
    const depths = await queueDepths([requestQueue, resultQueue])
    if (depths.every(({ ready, unacknowledged }) =>
      ready === 0 && unacknowledged === 0)) return
    await delay(100)
  }
  throw new Error('Verification broker queues did not drain before the deadline.')
}

async function readDeadLetter() {
  let connection: ChannelModel | undefined
  try {
    connection = await connect(brokerUrl)
    const channel = await connection.createChannel()
    const message = await channel.get(requestDeadLetterQueue, { noAck: false })
    if (!message) return undefined

    const errorCode = message.properties.headers?.['x-caseledger-error-code']
    const result = {
      body: message.content.toString('utf8'),
      errorCode: typeof errorCode === 'string' ? errorCode : '',
      messageId: message.properties.messageId,
    }
    channel.ack(message)
    return result
  } finally {
    await connection?.close().catch(() => undefined)
  }
}

async function withConfirmChannel(action: (channel: ConfirmChannel) => Promise<void>) {
  const connection = await connect(brokerUrl)
  try {
    const channel = await connection.createConfirmChannel()
    await action(channel)
  } finally {
    await connection.close().catch(() => undefined)
  }
}

async function queueDepths(queueNames: string[]) {
  return Promise.all(queueNames.map(async (queueName) => {
    const response = await fetch(
      `${managementUrl}/api/queues/%2F/${encodeURIComponent(queueName)}`,
      { headers: { authorization: managementAuthorization } },
    )
    if (!response.ok) {
      throw new Error(`RabbitMQ queue query failed with status ${response.status}.`)
    }
    const value = await response.json() as {
      messages_ready?: unknown
      messages_unacknowledged?: unknown
    }
    if (typeof value.messages_ready !== 'number' ||
      typeof value.messages_unacknowledged !== 'number') {
      throw new Error('RabbitMQ returned an invalid queue depth response.')
    }
    return {
      ready: value.messages_ready,
      unacknowledged: value.messages_unacknowledged,
    }
  }))
}
