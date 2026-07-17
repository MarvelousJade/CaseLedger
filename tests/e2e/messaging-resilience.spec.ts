import { randomUUID } from 'node:crypto'
import { expect, test } from '@playwright/test'
import { publishInvalidVerificationRequest, waitForDeadLetter } from './support/broker'

test('the worker dead-letters an invalid verification contract', async () => {
  const messageId = randomUUID()
  const body = Buffer.from('{"schemaVersion":1}', 'utf8')

  await publishInvalidVerificationRequest({
    messageId,
    correlationId: `e2e-${messageId}`,
    body,
  })
  const deadLetter = await waitForDeadLetter()

  expect(deadLetter.messageId).toBe(messageId)
  expect(deadLetter.errorCode).toBe('SCHEMA_INVALID')
  expect(deadLetter.body).toBe(body.toString('utf8'))
})
