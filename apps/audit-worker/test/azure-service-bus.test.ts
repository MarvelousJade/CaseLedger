import assert from "node:assert/strict";
import test from "node:test";
import type {
  MessageHandlers,
  ServiceBusMessage,
  ServiceBusReceivedMessage,
  ServiceBusReceiver,
  ServiceBusSender,
  SubscribeOptions,
} from "@azure/service-bus";
import { AUDIT_GENESIS_HASH, computeAuditHash } from "@caseledger/audit-verifier";
import { startAzureServiceBusHost } from "../src/azure-service-bus-host.ts";
import {
  AzureServiceBusDelivery,
  AzureServiceBusResultPublisher,
  safeDeadLetterReason,
  type ServiceBusClientLike,
} from "../src/azure-service-bus.ts";
import type { WorkerConfig } from "../src/config.ts";
import type {
  VerificationRequest,
  VerificationResultMessage,
} from "../src/contracts.ts";
import type { HealthState } from "../src/health.ts";
import type { Logger } from "../src/logger.ts";
import { ResultOutboxPublisher } from "../src/outbox-publisher.ts";
import { VerificationProcessor } from "../src/processor.ts";
import type { ResultRepository, StoredResult } from "../src/repository.ts";
import type { RetryTier } from "../src/topology.ts";

const logger: Logger = { log() {} };

function validRequest(): VerificationRequest {
  const eventId = "11111111-1111-4111-8111-111111111111";
  const caseId = "91ad19ce-a910-4031-aa59-3c256f31ef67";
  const actorId = "33333333-3333-4333-8333-333333333333";
  const createdAt = "2026-07-16T18:00:00.0000000Z";
  const canonicalData = JSON.stringify({
    version: 1,
    eventId,
    caseId,
    sequence: 1,
    eventType: "case.created",
    description: "Case created",
    actorId,
    actorName: "Analyst",
    createdAt,
    data: {},
  });
  const hash = computeAuditHash(AUDIT_GENESIS_HASH, canonicalData);
  const jobId = "184e1670-4ac9-4b30-beda-2b78f0d15a77";
  return {
    schemaVersion: 1,
    messageType: "caseledger.audit.verification.requested.v1",
    messageId: jobId,
    jobId,
    correlationId: "trace-service-bus",
    requestedAt: "2026-07-16T18:00:00.000Z",
    data: {
      caseId,
      verificationProfile: "export-chain-v1",
      targetSequence: 1,
      targetHash: hash,
      snapshot: {
        eventCount: 1,
        events: [
          {
            eventId,
            caseId,
            sequence: 1,
            eventType: "case.created",
            description: "Case created",
            actorId,
            actorName: "Analyst",
            createdAt,
            previousHash: AUDIT_GENESIS_HASH,
            hash,
            canonicalData,
          },
        ],
      },
    },
  };
}

function receivedMessage(request = validRequest(), attempt = 0): ServiceBusReceivedMessage {
  return {
    body: Buffer.from(JSON.stringify(request), "utf8"),
    messageId: request.jobId,
    correlationId: request.correlationId,
    contentType: "application/json",
    subject: request.messageType,
    applicationProperties: {
      "x-caseledger-attempt": attempt,
      traceparent: "00-c26f5f3ea6e9b92179d4ba1e5d49e346-6ba7b8105662f35c-01",
    },
  } as unknown as ServiceBusReceivedMessage;
}

function validResult(): VerificationResultMessage {
  return {
    schemaVersion: 1,
    messageType: "caseledger.audit.verification.result.v1",
    resultId: "audit-verification:184e1670-4ac9-4b30-beda-2b78f0d15a77:v1",
    jobId: "184e1670-4ac9-4b30-beda-2b78f0d15a77",
    correlationId: "trace-service-bus",
    caseId: "91ad19ce-a910-4031-aa59-3c256f31ef67",
    verificationProfile: "export-chain-v1",
    snapshotSha256: "a".repeat(64),
    completedAt: "2026-07-16T18:00:00.000Z",
    outcome: "valid",
    checkedEvents: 1,
    chainHead: "b".repeat(64),
    brokenAt: null,
    error: null,
    attempt: 0,
    workerVersion: "test",
  };
}

test("schedules retry copies and manually settles or dead-letters deliveries", async () => {
  const completed: ServiceBusReceivedMessage[] = [];
  const deadLetters: Array<{ reason: string | undefined }> = [];
  const scheduled: Array<{ message: ServiceBusMessage; at: Date }> = [];
  const receiver = {
    async completeMessage(message: ServiceBusReceivedMessage) {
      completed.push(message);
    },
    async deadLetterMessage(
      _message: ServiceBusReceivedMessage,
      options?: { deadLetterReason: string; deadLetterErrorDescription: string },
    ) {
      deadLetters.push({ reason: options?.deadLetterReason });
    },
  };
  const sender = {
    async scheduleMessages(message: ServiceBusMessage, at: Date) {
      scheduled.push({ message, at });
      return [];
    },
  };
  const now = new Date("2026-07-16T18:00:00.000Z");
  const message = receivedMessage();
  const delivery = new AzureServiceBusDelivery(message, receiver, sender, () => now);
  const tier: RetryTier = {
    queue: "unused-by-service-bus",
    routingKey: "unused-by-service-bus",
    delayMilliseconds: 5_000,
  };

  await delivery.scheduleRetry(validRequest(), tier, 1);
  await delivery.complete();
  await delivery.deadLetter("SCHEMA_INVALID");

  assert.equal(delivery.attempt, 0);
  assert.equal(scheduled.length, 1);
  assert.equal(scheduled[0]!.at.toISOString(), "2026-07-16T18:00:05.000Z");
  assert.equal(
    scheduled[0]!.message.messageId,
    `${validRequest().jobId}:retry:1`,
  );
  assert.equal(scheduled[0]!.message.applicationProperties?.["x-caseledger-attempt"], 1);
  assert.equal(completed.length, 1);
  assert.deepEqual(deadLetters, [{ reason: "SCHEMA_INVALID" }]);
  assert.equal(safeDeadLetterReason("bad reason!?"), "BAD_REASON__");
});

test("publishes deterministic result messages through a Service Bus sender", async () => {
  const sent: ServiceBusMessage[] = [];
  const sender = {
    async sendMessages(message: ServiceBusMessage) {
      sent.push(message);
    },
  };
  const result = validResult();

  await new AzureServiceBusResultPublisher(sender).publishResult(result);
  assert.equal(sent.length, 1);
  assert.equal(sent[0]!.messageId, result.resultId);
  assert.equal(sent[0]!.subject, result.messageType);
  assert.deepEqual(JSON.parse(Buffer.from(sent[0]!.body).toString("utf8")), result);
});

test("Service Bus result publication is driven by the durable outbox", async () => {
  const result = validResult();
  const order: string[] = [];
  const controller = new AbortController();
  let claimed = false;
  const repository = {
    async claimOutbox() {
      if (claimed) {
        return [];
      }
      claimed = true;
      return [
        {
          resultId: result.resultId,
          jobId: result.jobId,
          routingKey: "audit.verification.result.v1",
          result,
          lockId: "56165c5e-3c22-44bf-aed1-34e82f9926f3",
          publishAttempts: 0,
        },
      ];
    },
    async markPublished() {
      order.push("mark-published");
      controller.abort();
      return true;
    },
    async releaseOutbox() {
      order.push("release");
    },
  };
  const sender = {
    async sendMessages() {
      order.push("service-bus-send");
    },
  };
  const outbox = new ResultOutboxPublisher(
    repository as never,
    new AzureServiceBusResultPublisher(sender),
    logger,
    { batchSize: 1, leaseSeconds: 30, pollMilliseconds: 1 },
  );

  await outbox.run(controller.signal);
  assert.deepEqual(order, ["service-bus-send", "mark-published"]);
});

test("host binds the configured topic subscription with manual settlement", async () => {
  let handlers: MessageHandlers | undefined;
  let subscribeOptions: SubscribeOptions | undefined;
  const receiver = {
    subscribe(value: MessageHandlers, options?: SubscribeOptions) {
      handlers = value;
      subscribeOptions = options;
      return { async close() {} };
    },
    async completeMessage() {},
    async abandonMessage() {},
    async deadLetterMessage() {},
    async close() {},
  } as unknown as ServiceBusReceiver;
  const topicSender = {
    async scheduleMessages() {
      return [];
    },
    async sendMessages() {},
    async close() {},
  } as unknown as ServiceBusSender;
  const receiverCalls: string[][] = [];
  const senderCalls: string[] = [];
  const client: ServiceBusClientLike = {
    createReceiver(topic, subscription) {
      receiverCalls.push([topic, subscription]);
      return receiver;
    },
    createSender(topic) {
      senderCalls.push(topic);
      return topicSender;
    },
    async close() {},
  };
  const resultRepository: ResultRepository = {
    async storeResult(
      _jobId,
      _fingerprint,
      _snapshotSha256,
      result,
    ): Promise<StoredResult> {
      return { disposition: "stored", result };
    },
  };
  const outboxRepository = {
    async claimOutbox() {
      return [];
    },
    async markPublished() {
      return true;
    },
    async releaseOutbox() {},
  };
  const config: WorkerConfig = {
    broker: {
      provider: "azure-service-bus",
      connectionString: "not-logged",
      topic: "audit-messages",
      requestSubscription: "audit-worker",
    },
    databaseUrl: "not-used",
    healthHost: "127.0.0.1",
    healthPort: 8081,
    prefetch: 4,
    maximumMessageBytes: 1024 * 1024,
    outboxPollMilliseconds: 10,
    outboxBatchSize: 10,
    outboxLeaseSeconds: 30,
    workerVersion: "test",
  };
  const health: HealthState = {
    database: true,
    broker: false,
    brokerProvider: "azure-service-bus",
  };
  if (config.broker.provider !== "azure-service-bus") {
    throw new Error("test configuration must use Azure Service Bus");
  }
  const runtime = await startAzureServiceBusHost(
    config.broker,
    {
      config,
      repository: outboxRepository as never,
      processor: new VerificationProcessor(resultRepository, { workerVersion: "test" }),
      logger,
      health,
    },
    () => client,
  );

  try {
    assert.deepEqual(receiverCalls, [["audit-messages", "audit-worker"]]);
    assert.deepEqual(senderCalls, ["audit-messages"]);
    assert.equal(subscribeOptions?.autoCompleteMessages, false);
    assert.equal(subscribeOptions?.maxConcurrentCalls, 4);
    assert.equal(health.broker, true);
    assert.ok(handlers);
    await handlers!.processMessage(receivedMessage());
  } finally {
    await runtime.close();
  }
});
