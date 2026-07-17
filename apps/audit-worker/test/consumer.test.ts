import assert from "node:assert/strict";
import test from "node:test";
import type { ConsumeMessage } from "amqplib";
import { AUDIT_GENESIS_HASH, computeAuditHash } from "@caseledger/audit-verifier";
import { DeliveryHandler, type Acknowledger } from "../src/consumer.ts";
import type {
  VerificationRequest,
  VerificationResultMessage,
} from "../src/contracts.ts";
import type { Logger } from "../src/logger.ts";
import { VerificationProcessor } from "../src/processor.ts";
import type { TransportPublisher } from "../src/rabbit-publisher.ts";
import type { ResultRepository, StoredResult } from "../src/repository.ts";
import type { RetryTier } from "../src/topology.ts";

const logger: Logger = { log() {} };

class RecordingAcknowledger implements Acknowledger {
  readonly messages: ConsumeMessage[] = [];
  ack(message: ConsumeMessage): void {
    this.messages.push(message);
  }
}

class RecordingPublisher implements TransportPublisher {
  readonly calls: string[] = [];

  async publishRetry(
    _message: ConsumeMessage,
    _request: VerificationRequest,
    tier: RetryTier,
    nextAttempt: number,
  ): Promise<void> {
    this.calls.push(`retry:${tier.delayMilliseconds}:${nextAttempt}`);
  }

  async publishDeadLetter(
    _message: ConsumeMessage,
    code: string,
    _attempt: number,
  ): Promise<void> {
    this.calls.push(`dead:${code}`);
  }

  async publishResult(result: VerificationResultMessage): Promise<void> {
    this.calls.push(`result:${result.outcome}:${result.error?.code ?? "none"}`);
  }
}

class FailingRepository implements ResultRepository {
  async storeResult(): Promise<StoredResult> {
    throw new Error("temporary storage failure");
  }
}

function validRequest(): VerificationRequest {
  const canonicalData = '{"eventType":"case.created"}';
  const hash = computeAuditHash(AUDIT_GENESIS_HASH, canonicalData);
  const jobId = "184e1670-4ac9-4b30-beda-2b78f0d15a77";
  return {
    schemaVersion: 1,
    messageType: "caseledger.audit.verification.requested.v1",
    messageId: jobId,
    jobId,
    correlationId: "trace-01HZY6EFKYM7JKQ8YVZ5M2DTWK",
    requestedAt: "2026-07-16T18:00:00.000Z",
    data: {
      caseId: "91ad19ce-a910-4031-aa59-3c256f31ef67",
      verificationProfile: "export-chain-v1",
      targetSequence: 1,
      targetHash: hash,
      snapshot: {
        eventCount: 1,
        events: [{ sequence: 1, previousHash: AUDIT_GENESIS_HASH, hash, canonicalData }],
      },
    },
  };
}

function delivery(request: VerificationRequest, attempt = 0): ConsumeMessage {
  return {
    content: Buffer.from(JSON.stringify(request), "utf8"),
    fields: {
      consumerTag: "test-consumer",
      deliveryTag: 1,
      redelivered: false,
      exchange: "caseledger.audit",
      routingKey: "audit.verification.requested.v1",
    },
    properties: {
      contentType: "application/json",
      contentEncoding: "utf-8",
      headers: { "x-caseledger-attempt": attempt },
      deliveryMode: 2,
      priority: undefined,
      correlationId: request.correlationId,
      replyTo: undefined,
      expiration: undefined,
      messageId: request.jobId,
      timestamp: 1_784_226_000,
      type: request.messageType,
      userId: undefined,
      appId: "caseledger-api",
      clusterId: undefined,
    },
  };
}

function handler(
  repository: ResultRepository,
  acknowledger: RecordingAcknowledger,
  publisher: RecordingPublisher,
): DeliveryHandler {
  return new DeliveryHandler(
    acknowledger,
    publisher,
    new VerificationProcessor(repository, { workerVersion: "test" }),
    logger,
    { maximumMessageBytes: 1024 * 1024, workerVersion: "test" },
  );
}

test("persists the result before acknowledging the request", async () => {
  let resolveStore: ((value: StoredResult) => void) | undefined;
  const storePromise = new Promise<StoredResult>((resolve) => {
    resolveStore = resolve;
  });
  const repository: ResultRepository = { storeResult: () => storePromise };
  const acknowledger = new RecordingAcknowledger();
  const publisher = new RecordingPublisher();
  const request = validRequest();
  const message = delivery(request);

  const handling = handler(repository, acknowledger, publisher).handle(message);
  await Promise.resolve();
  assert.equal(acknowledger.messages.length, 0);

  const processor = new VerificationProcessor(
    {
      async storeResult(_jobId, _digest, result) {
        return { disposition: "stored", result };
      },
    },
    { workerVersion: "test" },
  );
  const stored = await processor.process(request, 0);
  resolveStore!(stored);
  await handling;
  assert.equal(acknowledger.messages.length, 1);
  assert.deepEqual(publisher.calls, []);
});

test("routes attempts through 5s, 30s, and 5m before terminal handling", async () => {
  const expected = [
    "retry:5000:1",
    "retry:30000:2",
    "retry:300000:3",
  ];

  for (let attempt = 0; attempt < expected.length; attempt += 1) {
    const acknowledger = new RecordingAcknowledger();
    const publisher = new RecordingPublisher();
    await handler(new FailingRepository(), acknowledger, publisher).handle(
      delivery(validRequest(), attempt),
    );
    assert.deepEqual(publisher.calls, [expected[attempt]]);
    assert.equal(acknowledger.messages.length, 1);
  }
});

test("publishes terminal error result before the dead-letter copy", async () => {
  const acknowledger = new RecordingAcknowledger();
  const publisher = new RecordingPublisher();
  await handler(new FailingRepository(), acknowledger, publisher).handle(
    delivery(validRequest(), 3),
  );

  assert.deepEqual(publisher.calls, [
    "result:error:RETRY_EXHAUSTED",
    "dead:RETRY_EXHAUSTED",
  ]);
  assert.equal(acknowledger.messages.length, 1);
});

test("dead-letters malformed JSON directly and then acknowledges", async () => {
  const acknowledger = new RecordingAcknowledger();
  const publisher = new RecordingPublisher();
  const message = delivery(validRequest());
  message.content = Buffer.from("not-json");

  await handler(new FailingRepository(), acknowledger, publisher).handle(message);
  assert.deepEqual(publisher.calls, ["dead:INVALID_JSON"]);
  assert.equal(acknowledger.messages.length, 1);
});
