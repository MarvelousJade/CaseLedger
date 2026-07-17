import assert from "node:assert/strict";
import test from "node:test";
import { AUDIT_GENESIS_HASH, computeAuditHash } from "@caseledger/audit-verifier";
import {
  MessageContractError,
  computeSnapshotSha256,
  parseVerificationRequest,
  resultIdFor,
  type VerificationRequest,
} from "../src/contracts.ts";

function request(): VerificationRequest {
  const canonicalData = '{"eventType":"case.created"}';
  const hash = computeAuditHash(AUDIT_GENESIS_HASH, canonicalData);
  const jobId = "184e1670-4ac9-4b30-beda-2b78f0d15a77";

  return {
    schemaVersion: 1,
    messageType: "caseledger.audit.verification.requested.v1",
    messageId: jobId,
    jobId,
    correlationId: "56165c5e-3c22-44bf-aed1-34e82f9926f3",
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

test("parses the locked v1 request and derives stable identifiers", () => {
  const input = request();
  const parsed = parseVerificationRequest(Buffer.from(JSON.stringify(input)), 1024 * 1024);

  assert.deepEqual(parsed, input);
  assert.equal(
    resultIdFor(input.jobId),
    `audit-verification:${input.jobId}:v1`,
  );
  assert.equal(computeSnapshotSha256(input.data.snapshot.events).length, 64);
  assert.equal(
    computeSnapshotSha256(input.data.snapshot.events),
    computeSnapshotSha256(structuredClone(input).data.snapshot.events),
  );
});

test("uses the cross-language framed digest for non-ASCII canonical data", () => {
  const events = [
    {
      sequence: 1,
      previousHash: "0".repeat(64),
      hash: "a".repeat(64),
      canonicalData: '{"label":"Montréal <>& 雪"}',
    },
  ];

  assert.equal(
    computeSnapshotSha256(events),
    "8ff62c4c7238c75b4602d324227912252fcbbb22beb788db36877b662b271eac",
  );
});

test("rejects a mismatched message id without echoing the body", () => {
  const input = request();
  input.messageId = "4f8827a1-c8e7-4ec1-ab7b-9570cf5b144e";

  assert.throws(
    () => parseVerificationRequest(Buffer.from(JSON.stringify(input)), 1024 * 1024),
    (error: unknown) =>
      error instanceof MessageContractError &&
      error.code === "SCHEMA_INVALID" &&
      !error.message.includes(input.data.snapshot.events[0]!.canonicalData),
  );
});

test("rejects oversized and malformed messages with structured codes", () => {
  assert.throws(
    () => parseVerificationRequest(Buffer.from("{}"), 1),
    (error: unknown) =>
      error instanceof MessageContractError && error.code === "MESSAGE_TOO_LARGE",
  );
  assert.throws(
    () => parseVerificationRequest(Buffer.from("not-json"), 1024),
    (error: unknown) =>
      error instanceof MessageContractError && error.code === "INVALID_JSON",
  );
});
