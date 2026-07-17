import assert from "node:assert/strict";
import test from "node:test";
import type { VerificationResultMessage } from "../src/contracts.ts";
import {
  IdempotencyConflictError,
  PostgresResultRepository,
} from "../src/repository.ts";

const jobId = "184e1670-4ac9-4b30-beda-2b78f0d15a77";

function result(): VerificationResultMessage {
  return {
    schemaVersion: 1,
    messageType: "caseledger.audit.verification.result.v1",
    resultId: `audit-verification:${jobId}:v1`,
    jobId,
    correlationId: "trace-id",
    caseId: "20000000-0000-0000-0000-000000000001",
    verificationProfile: "export-chain-v1",
    snapshotSha256: "b".repeat(64),
    completedAt: "2026-07-16T18:00:00.000Z",
    outcome: "valid",
    checkedEvents: 0,
    chainHead: null,
    brokenAt: null,
    error: null,
    attempt: 0,
    workerVersion: "test",
  };
}

test("migration adds and fail-closed backfills versioned request fingerprints", async () => {
  const queries: string[] = [];
  const pool = {
    async query(sql: string) {
      queries.push(sql);
      return { rows: [], rowCount: 0 };
    },
  };
  await new PostgresResultRepository(pool as never).migrate();

  assert.equal(queries.length, 2);
  const additive = queries[1]!;
  assert.match(additive, /ADD COLUMN IF NOT EXISTS request_fingerprint char\(64\)/);
  assert.match(additive, /ADD COLUMN IF NOT EXISTS fingerprint_version smallint/);
  assert.match(additive, /SET request_fingerprint = snapshot_sha256/);
  assert.match(additive, /fingerprint_version = 0/);
  assert.match(additive, /ALTER COLUMN request_fingerprint SET NOT NULL/);
  assert.doesNotMatch(additive, /DROP TABLE|TRUNCATE/i);
});

test("legacy fingerprint rows fail closed instead of adopting a new request", async () => {
  let rolledBack = false;
  let released = false;
  const client = {
    async query(sql: string) {
      if (sql.includes("INSERT INTO audit_worker.inbox")) {
        return { rows: [], rowCount: 0 };
      }
      if (sql.includes("SELECT request_fingerprint")) {
        return {
          rows: [
            {
              request_fingerprint: "b".repeat(64),
              fingerprint_version: 0,
              result_payload: {},
            },
          ],
          rowCount: 1,
        };
      }
      if (sql === "ROLLBACK") {
        rolledBack = true;
      }
      return { rows: [], rowCount: 0 };
    },
    release() {
      released = true;
    },
  };
  const pool = { async connect() { return client; } };
  const repository = new PostgresResultRepository(pool as never);

  await assert.rejects(
    () => repository.storeResult(jobId, "a".repeat(64), "b".repeat(64), result()),
    (error: unknown) => error instanceof IdempotencyConflictError,
  );
  assert.equal(rolledBack, true);
  assert.equal(released, true);
});

test("new inbox rows persist fingerprint separately from the API snapshot digest", async () => {
  let inboxParameters: unknown[] | undefined;
  const client = {
    async query(sql: string, parameters?: unknown[]) {
      if (sql.includes("INSERT INTO audit_worker.inbox")) {
        inboxParameters = parameters;
        return { rows: [{ job_id: jobId }], rowCount: 1 };
      }
      return { rows: [], rowCount: 1 };
    },
    release() {},
  };
  const pool = { async connect() { return client; } };
  const repository = new PostgresResultRepository(pool as never);

  await repository.storeResult(jobId, "a".repeat(64), "b".repeat(64), result());
  assert.equal(inboxParameters?.[1], "a".repeat(64));
  assert.equal(inboxParameters?.[2], "b".repeat(64));
});
