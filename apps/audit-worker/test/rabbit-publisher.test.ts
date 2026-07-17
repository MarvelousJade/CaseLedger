import assert from "node:assert/strict";
import test from "node:test";
import type { ConfirmChannel, Options } from "amqplib";
import type { VerificationResultMessage } from "../src/contracts.ts";
import { RabbitTransportPublisher } from "../src/rabbit-publisher.ts";

test("publishes persistent results with AMQP timestamps in epoch seconds", async () => {
  const publishes: Array<{ options: Options.Publish }> = [];
  let confirms = 0;
  const channel = {
    publish(
      _exchange: string,
      _routingKey: string,
      _content: Buffer,
      options: Options.Publish,
    ) {
      publishes.push({ options });
      return true;
    },
    async waitForConfirms() {
      confirms += 1;
    },
  } as unknown as ConfirmChannel;
  const completedAt = "2026-07-16T18:00:00.000Z";
  const result: VerificationResultMessage = {
    schemaVersion: 1,
    messageType: "caseledger.audit.verification.result.v1",
    resultId: "audit-verification:184e1670-4ac9-4b30-beda-2b78f0d15a77:v1",
    jobId: "184e1670-4ac9-4b30-beda-2b78f0d15a77",
    correlationId: "trace-id",
    caseId: "91ad19ce-a910-4031-aa59-3c256f31ef67",
    verificationProfile: "export-chain-v1",
    snapshotSha256: "a".repeat(64),
    completedAt,
    outcome: "valid",
    checkedEvents: 1,
    chainHead: "b".repeat(64),
    brokenAt: null,
    error: null,
    attempt: 0,
    workerVersion: "test",
  };

  await new RabbitTransportPublisher(channel).publishResult(result);

  assert.equal(publishes.length, 1);
  assert.equal(publishes[0]!.options.timestamp, Math.floor(Date.parse(completedAt) / 1_000));
  assert.equal(publishes[0]!.options.deliveryMode, 2);
  assert.equal(publishes[0]!.options.messageId, result.resultId);
  assert.equal(confirms, 1);
});
