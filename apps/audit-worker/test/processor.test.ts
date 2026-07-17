import assert from "node:assert/strict";
import test from "node:test";
import {
  AUDIT_GENESIS_HASH,
  computeAuditHash,
} from "@caseledger/audit-verifier";
import {
  computeSnapshotSha256,
  type VerificationRequest,
  type VerificationResultMessage,
} from "../src/contracts.ts";
import { VerificationProcessor } from "../src/processor.ts";
import {
  IdempotencyConflictError,
  type ResultRepository,
  type StoredResult,
} from "../src/repository.ts";

class MemoryResultRepository implements ResultRepository {
  readonly rows = new Map<
    string,
    { fingerprint: string; snapshotSha256: string; result: VerificationResultMessage }
  >();

  async storeResult(
    jobId: string,
    requestFingerprint: string,
    snapshotSha256: string,
    result: VerificationResultMessage,
  ): Promise<StoredResult> {
    const existing = this.rows.get(jobId);
    if (existing !== undefined) {
      if (existing.fingerprint !== requestFingerprint) {
        throw new IdempotencyConflictError(jobId);
      }
      return { disposition: "cached", result: existing.result };
    }

    this.rows.set(jobId, { fingerprint: requestFingerprint, snapshotSha256, result });
    return { disposition: "stored", result };
  }
}

function validRequest(): VerificationRequest {
  const eventId = "11111111-1111-4111-8111-111111111111";
  const caseId = "91ad19ce-a910-4031-aa59-3c256f31ef67";
  const actorId = "33333333-3333-4333-8333-333333333333";
  const eventType = "case.created";
  const description = "Case created";
  const actorName = "Analyst";
  const createdAt = "2026-07-16T18:00:00.0000000Z";
  const canonicalData = JSON.stringify({
    version: 1,
    eventId,
    caseId,
    sequence: 1,
    eventType,
    description,
    actorId,
    actorName,
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
    correlationId: "00-c26f5f3ea6e9b92179d4ba1e5d49e346-6ba7b8105662f35c-01",
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
            eventType,
            description,
            actorId,
            actorName,
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

test("rejects a projected description changed without changing canonical data", async () => {
  const repository = new MemoryResultRepository();
  const processor = new VerificationProcessor(repository, { workerVersion: "test" });
  const request = validRequest();
  request.data.snapshot.events[0]!.description = "Tampered description";

  const stored = await processor.process(request, 0);
  assert.equal(stored.result.outcome, "invalid");
  assert.equal(stored.result.error?.code, "CANONICAL_PROJECTION_MISMATCH");
  assert.equal(stored.result.checkedEvents, 1);
  assert.equal(stored.result.brokenAt, 1);
});

test("idempotency fingerprint rejects immutable request mutations with the same event digest", async (context) => {
  async function expectConflict(
    original: VerificationRequest,
    conflicting: VerificationRequest,
  ): Promise<void> {
    assert.equal(
      computeSnapshotSha256(original.data.snapshot.events),
      computeSnapshotSha256(conflicting.data.snapshot.events),
    );
    const repository = new MemoryResultRepository();
    const processor = new VerificationProcessor(repository, { workerVersion: "test" });
    await processor.process(original, 0);
    await assert.rejects(
      () => processor.process(conflicting, 1),
      (error: unknown) => error instanceof IdempotencyConflictError,
    );
  }

  await context.test("altered target", async () => {
    const original = validRequest();
    const conflicting = structuredClone(original);
    conflicting.data.targetSequence = 2;
    await expectConflict(original, conflicting);
  });

  await context.test("altered declared event count", async () => {
    const original = validRequest();
    const conflicting = structuredClone(original);
    conflicting.data.snapshot.eventCount = 2;
    await expectConflict(original, conflicting);
  });

  await context.test("altered received event order", async () => {
    const original = withSecondEvent(validRequest());
    const conflicting = structuredClone(original);
    conflicting.data.snapshot.events.reverse();
    await expectConflict(original, conflicting);
  });

  await context.test("altered case id on an empty snapshot", async () => {
    const original = emptyRequest("20000000-0000-0000-0000-000000000001");
    const conflicting = emptyRequest("20000000-0000-0000-0000-000000000002");
    await expectConflict(original, conflicting);
  });
});

function withSecondEvent(value: VerificationRequest): VerificationRequest {
  const result = structuredClone(value);
  const first = result.data.snapshot.events[0]!;
  const eventId = "44444444-4444-4444-4444-444444444444";
  const createdAt = "2026-07-16T18:01:00.0000000Z";
  const canonicalData = JSON.stringify({
    version: 1,
    eventId,
    caseId: result.data.caseId,
    sequence: 2,
    eventType: "case.updated",
    description: "Case updated",
    actorId: first.actorId,
    actorName: first.actorName,
    createdAt,
    data: {},
  });
  const second = {
    eventId,
    caseId: result.data.caseId,
    sequence: 2,
    eventType: "case.updated",
    description: "Case updated",
    actorId: first.actorId,
    actorName: first.actorName,
    createdAt,
    previousHash: first.hash,
    hash: computeAuditHash(first.hash, canonicalData),
    canonicalData,
  };
  result.data.snapshot.events.push(second);
  result.data.snapshot.eventCount = 2;
  result.data.targetSequence = 2;
  result.data.targetHash = second.hash;
  return result;
}

function emptyRequest(caseId: string): VerificationRequest {
  const result = validRequest();
  result.data.caseId = caseId;
  result.data.targetSequence = 0;
  result.data.targetHash = AUDIT_GENESIS_HASH;
  result.data.snapshot.eventCount = 0;
  result.data.snapshot.events = [];
  return result;
}
