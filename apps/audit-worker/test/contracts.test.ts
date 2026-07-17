import assert from "node:assert/strict";
import test from "node:test";
import { AUDIT_GENESIS_HASH, computeAuditHash } from "@caseledger/audit-verifier";
import {
  MessageContractError,
  computeRequestFingerprint,
  computeSnapshotSha256,
  parseVerificationRequest,
  resultIdFor,
  type VerificationRequest,
} from "../src/contracts.ts";

function request(): VerificationRequest {
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
    correlationId: "56165c5e-3c22-44bf-aed1-34e82f9926f3",
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
      eventId: "11111111-1111-4111-8111-111111111111",
      caseId: "22222222-2222-4222-8222-222222222222",
      sequence: 1,
      eventType: "case.created",
      description: "Montréal <>& 雪",
      actorId: "33333333-3333-4333-8333-333333333333",
      actorName: "Élodie",
      createdAt: "2026-07-16T18:00:00.0000000Z",
      previousHash: "0".repeat(64),
      hash: "a".repeat(64),
      canonicalData: '{"label":"Montréal <>& 雪"}',
    },
  ];

  assert.equal(
    computeSnapshotSha256(events),
    "0b341c88308bd99a76b00352fe951a615330212a8a4f2f1061aaadd14ba68a90",
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

test("requires lowercase D GUIDs and exact UTC round-trip timestamps", () => {
  const seededGuid = request();
  seededGuid.data.snapshot.events[0]!.eventId =
    "30000000-0000-0000-0000-000000000001";
  assert.doesNotThrow(() =>
    parseVerificationRequest(Buffer.from(JSON.stringify(seededGuid)), 1024 * 1024),
  );

  const uppercaseGuid = request();
  uppercaseGuid.data.snapshot.events[0]!.eventId =
    "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA";
  assert.throws(
    () => parseVerificationRequest(Buffer.from(JSON.stringify(uppercaseGuid)), 1024 * 1024),
    (error: unknown) =>
      error instanceof MessageContractError && error.code === "SCHEMA_INVALID",
  );

  const shortTimestamp = request();
  shortTimestamp.data.snapshot.events[0]!.createdAt = "2026-07-16T18:00:00Z";
  assert.throws(
    () => parseVerificationRequest(Buffer.from(JSON.stringify(shortTimestamp)), 1024 * 1024),
    (error: unknown) =>
      error instanceof MessageContractError && error.code === "SCHEMA_INVALID",
  );
});

test("requires lowercase D message and job IDs", () => {
  const uppercaseMessageId = request();
  uppercaseMessageId.messageId = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA";
  assert.throws(
    () =>
      parseVerificationRequest(
        Buffer.from(JSON.stringify(uppercaseMessageId)),
        1024 * 1024,
      ),
    (error: unknown) =>
      error instanceof MessageContractError &&
      error.code === "SCHEMA_INVALID" &&
      error.message.startsWith("messageId:"),
  );

  const uppercaseJobId = request();
  uppercaseJobId.jobId = "BBBBBBBB-BBBB-4BBB-8BBB-BBBBBBBBBBBB";
  assert.throws(
    () =>
      parseVerificationRequest(Buffer.from(JSON.stringify(uppercaseJobId)), 1024 * 1024),
    (error: unknown) =>
      error instanceof MessageContractError &&
      error.code === "SCHEMA_INVALID" &&
      error.message.startsWith("jobId:"),
  );
});

test("keeps the deterministic result ID compatible with the API contract", () => {
  const jobId = "184e1670-4ac9-4b30-beda-2b78f0d15a77";
  assert.equal(
    resultIdFor(jobId),
    "audit-verification:184e1670-4ac9-4b30-beda-2b78f0d15a77:v1",
  );
});

test("full request fingerprint covers intent fields and received event order", () => {
  const original = request();
  const originalFingerprint = computeRequestFingerprint(original);
  const mutations: VerificationRequest[] = [];

  const target = structuredClone(original);
  target.data.targetHash = "f".repeat(64);
  mutations.push(target);

  const count = structuredClone(original);
  count.data.snapshot.eventCount += 1;
  mutations.push(count);

  const correlation = structuredClone(original);
  correlation.correlationId = "different-correlation";
  mutations.push(correlation);

  const requestedAt = structuredClone(original);
  requestedAt.requestedAt = "2026-07-16T18:00:01.000Z";
  mutations.push(requestedAt);

  for (const mutation of mutations) {
    assert.notEqual(computeRequestFingerprint(mutation), originalFingerprint);
  }

  const twoEvents = withSecondEvent(original);
  const reversed = structuredClone(twoEvents);
  reversed.data.snapshot.events.reverse();
  assert.equal(
    computeSnapshotSha256(twoEvents.data.snapshot.events),
    computeSnapshotSha256(reversed.data.snapshot.events),
  );
  assert.notEqual(
    computeRequestFingerprint(twoEvents),
    computeRequestFingerprint(reversed),
  );

  const emptyA = emptyRequest("20000000-0000-0000-0000-000000000001");
  const emptyB = emptyRequest("20000000-0000-0000-0000-000000000002");
  assert.equal(
    computeSnapshotSha256(emptyA.data.snapshot.events),
    computeSnapshotSha256(emptyB.data.snapshot.events),
  );
  assert.notEqual(computeRequestFingerprint(emptyA), computeRequestFingerprint(emptyB));
});

function withSecondEvent(value: VerificationRequest): VerificationRequest {
  const result = structuredClone(value);
  const first = result.data.snapshot.events[0]!;
  const eventId = "44444444-4444-4444-4444-444444444444";
  const canonicalData = JSON.stringify({
    version: 1,
    eventId,
    caseId: result.data.caseId,
    sequence: 2,
    eventType: "case.updated",
    description: "Case updated",
    actorId: first.actorId,
    actorName: first.actorName,
    createdAt: "2026-07-16T18:01:00.0000000Z",
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
    createdAt: "2026-07-16T18:01:00.0000000Z",
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
  const result = request();
  result.data.caseId = caseId;
  result.data.targetSequence = 0;
  result.data.targetHash = AUDIT_GENESIS_HASH;
  result.data.snapshot.eventCount = 0;
  result.data.snapshot.events = [];
  return result;
}
