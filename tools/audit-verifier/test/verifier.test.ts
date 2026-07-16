import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  AuditVerificationError,
  computeAuditHash,
  formatVerificationSummary,
  verifyAuditExport,
} from "../src/verifier.ts";

interface TestEvent {
  sequence: number;
  previousHash: string;
  hash: string;
  canonicalData: string;
}

function makeChain(canonicalValues: string[]): TestEvent[] {
  let previousHash = "GENESIS";

  return canonicalValues.map((canonicalData, index) => {
    const event = {
      sequence: index + 1,
      previousHash,
      hash: computeAuditHash(previousHash, canonicalData),
      canonicalData,
    };
    previousHash = event.hash;
    return event;
  });
}

const canonicalValues = [
  '{"action":"case.created","actor":"analyst@example.ca"}',
  '{"action":"evidence.added","file":"photo.jpg"}',
  '{"action":"case.status_changed","status":"review"}',
];

test("verifies a camelCase export envelope", () => {
  const events = makeChain(canonicalValues);
  const result = verifyAuditExport({
    caseId: "case-123",
    reference: "CL-2026-001",
    exportedAt: "2026-07-16T12:00:00.000Z",
    events,
  });

  assert.deepEqual(result, {
    count: 3,
    chainHead: events[2].hash,
  });
  assert.equal(
    formatVerificationSummary(result),
    `OK: verified 3 audit events; chain head ${events[2].hash}`,
  );
});

test("accepts a raw array with PascalCase fields", () => {
  const events = makeChain(canonicalValues).map((event) => ({
    Sequence: event.sequence,
    PreviousHash: event.previousHash,
    Hash: event.hash,
    CanonicalData: event.canonicalData,
  }));

  const result = verifyAuditExport(events);
  assert.equal(result.count, 3);
});

test("detects changed canonical data", () => {
  const events = makeChain(canonicalValues);
  events[1] = { ...events[1], canonicalData: `${events[1].canonicalData} ` };

  assert.throws(
    () => verifyAuditExport({ events }),
    (error: unknown) =>
      error instanceof AuditVerificationError &&
      /event #2: hash mismatch/.test(error.message),
  );
});

test("detects a broken previous-hash link", () => {
  const events = makeChain(canonicalValues);
  const replacementPreviousHash = "0".repeat(64);
  events[1] = {
    ...events[1],
    previousHash: replacementPreviousHash,
    hash: computeAuditHash(replacementPreviousHash, events[1].canonicalData),
  };

  assert.throws(
    () => verifyAuditExport(events),
    /event #2: previousHash does not match event #1 hash/,
  );
});

test("rejects missing or reordered sequence numbers", () => {
  const events = makeChain(canonicalValues);
  events[1] = { ...events[1], sequence: 3 };

  assert.throws(
    () => verifyAuditExport(events),
    /event #2: expected sequence 2, found 3/,
  );
});

test("CLI reports success and exits nonzero for tampering", () => {
  const directory = mkdtempSync(join(tmpdir(), "caseledger-verifier-"));
  const validPath = join(directory, "valid.json");
  const tamperedPath = join(directory, "tampered.json");
  const cliPath = fileURLToPath(new URL("../src/cli.ts", import.meta.url));
  const events = makeChain(canonicalValues);

  try {
    writeFileSync(validPath, JSON.stringify({ events }), "utf8");
    const validRun = spawnSync(process.execPath, [cliPath, validPath], {
      encoding: "utf8",
    });

    assert.equal(validRun.status, 0, validRun.stderr);
    assert.match(validRun.stdout, /^OK: verified 3 audit events;/);

    events[2] = { ...events[2], hash: "f".repeat(64) };
    writeFileSync(tamperedPath, JSON.stringify({ events }), "utf8");
    const tamperedRun = spawnSync(process.execPath, [cliPath, tamperedPath], {
      encoding: "utf8",
    });

    assert.equal(tamperedRun.status, 1);
    assert.match(tamperedRun.stderr, /Audit verification failed: event #3: hash mismatch/);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
