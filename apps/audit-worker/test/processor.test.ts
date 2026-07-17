import assert from "node:assert/strict";
import test from "node:test";
import {
  AUDIT_GENESIS_HASH,
  computeAuditHash,
} from "@caseledger/audit-verifier";
import type {
  VerificationRequest,
  VerificationResultMessage,
} from "../src/contracts.ts";
import { VerificationProcessor } from "../src/processor.ts";
import {
  IdempotencyConflictError,
  type ResultRepository,
  type StoredResult,
} from "../src/repository.ts";

class MemoryResultRepository implements ResultRepository {
  readonly rows = new Map<string, { digest: string; result: VerificationResultMessage }>();

  async storeResult(
    jobId: string,
    digest: string,
    result: VerificationResultMessage,
  ): Promise<StoredResult> {
    const existing = this.rows.get(jobId);
    if (existing !== undefined) {
      if (existing.digest !== digest) {
        throw new IdempotencyConflictError(jobId);
      }
      return { disposition: "cached", result: existing.result };
    }

    this.rows.set(jobId, { digest, result });
    return { disposition: "stored", result };
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
    correlationId: "00-c26f5f3ea6e9b92179d4ba1e5d49e346-6ba7b8105662f35c-01",
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

test("stores a valid result and reuses it for duplicate delivery", async () => {
  const repository = new MemoryResultRepository();
  let now = new Date("2026-07-16T18:01:00.000Z");
  const processor = new VerificationProcessor(repository, {
    workerVersion: "1.0.0-test",
    clock: () => now,
  });
  const request = validRequest();

  const first = await processor.process(request, 0);
  now = new Date("2026-07-16T18:02:00.000Z");
  const duplicate = await processor.process(request, 2);

  assert.equal(first.disposition, "stored");
  assert.equal(first.result.outcome, "valid");
  assert.equal(first.result.correlationId, request.correlationId);
  assert.equal(duplicate.disposition, "cached");
  assert.deepEqual(duplicate.result, first.result);
  assert.equal(repository.rows.size, 1);
});

test("treats the same job id with different snapshot content as poison", async () => {
  const repository = new MemoryResultRepository();
  const processor = new VerificationProcessor(repository, { workerVersion: "test" });
  const request = validRequest();
  await processor.process(request, 0);

  const conflicting = structuredClone(request);
  conflicting.data.snapshot.events[0]!.canonicalData += " ";

  await assert.rejects(
    () => processor.process(conflicting, 1),
    (error: unknown) =>
      error instanceof IdempotencyConflictError && error.jobId === request.jobId,
  );
});

test("returns terminal invalid results for chain and target failures", async () => {
  const repository = new MemoryResultRepository();
  const processor = new VerificationProcessor(repository, { workerVersion: "test" });
  const badGenesis = validRequest();
  badGenesis.data.snapshot.events[0]!.previousHash = "f".repeat(64);
  badGenesis.data.snapshot.events[0]!.hash = computeAuditHash(
    badGenesis.data.snapshot.events[0]!.previousHash,
    badGenesis.data.snapshot.events[0]!.canonicalData,
  );
  badGenesis.data.targetHash = badGenesis.data.snapshot.events[0]!.hash;

  const genesisResult = await processor.process(badGenesis, 0);
  assert.equal(genesisResult.result.outcome, "invalid");
  assert.equal(genesisResult.result.error?.code, "GENESIS_MISMATCH");

  const targetMismatch = validRequest();
  targetMismatch.jobId = "c4257995-6d15-4309-adfd-24e98ae7953a";
  targetMismatch.messageId = targetMismatch.jobId;
  targetMismatch.data.targetHash = "f".repeat(64);
  const targetResult = await processor.process(targetMismatch, 0);
  assert.equal(targetResult.result.outcome, "invalid");
  assert.equal(targetResult.result.error?.code, "TARGET_HASH_MISMATCH");
  assert.doesNotMatch(JSON.stringify(targetResult.result), /f{64}/);
});

test("enforces the declared snapshot event count", async () => {
  const repository = new MemoryResultRepository();
  const processor = new VerificationProcessor(repository, { workerVersion: "test" });
  const request = validRequest();
  request.data.snapshot.eventCount = 2;

  const stored = await processor.process(request, 0);
  assert.equal(stored.result.outcome, "invalid");
  assert.equal(stored.result.error?.code, "SNAPSHOT_EVENT_COUNT_MISMATCH");
});
