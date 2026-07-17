import { createHash } from "node:crypto";

type JsonObject = Record<string, unknown>;

export interface AuditEvent {
  sequence: number;
  previousHash: string;
  hash: string;
  canonicalData: string;
}

export interface VerificationResult {
  count: number;
  chainHead: string | null;
}

export const AUDIT_GENESIS_HASH = "0".repeat(64);

export type AuditVerificationErrorCode =
  | "INVALID_EXPORT"
  | "INVALID_EVENT"
  | "INVALID_TARGET"
  | "GENESIS_MISMATCH"
  | "SEQUENCE_MISMATCH"
  | "PREVIOUS_HASH_MISMATCH"
  | "HASH_MISMATCH"
  | "TARGET_SEQUENCE_MISMATCH"
  | "TARGET_HASH_MISMATCH";

export interface AuditVerificationTarget {
  targetSequence: number;
  targetHash: string;
}

export class AuditVerificationError extends Error {
  readonly code: AuditVerificationErrorCode;
  readonly checkedEvents: number;
  readonly brokenAt: number | null;

  constructor(
    code: AuditVerificationErrorCode,
    message: string,
    options: { checkedEvents?: number; brokenAt?: number | null } = {},
  ) {
    super(message);
    this.name = "AuditVerificationError";
    this.code = code;
    this.checkedEvents = options.checkedEvents ?? 0;
    this.brokenAt = options.brokenAt ?? null;
  }
}

const SHA256_HEX = /^[0-9a-f]{64}$/;

function isObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function getField(object: JsonObject, fieldName: string, context: string): unknown {
  const matchingKeys = Object.keys(object).filter(
    (key) => key.toLowerCase() === fieldName.toLowerCase(),
  );

  if (matchingKeys.length === 0) {
    throw new AuditVerificationError("INVALID_EVENT", `${context}: missing ${fieldName}`);
  }

  if (matchingKeys.length > 1) {
    throw new AuditVerificationError("INVALID_EVENT", `${context}: ambiguous ${fieldName} fields`);
  }

  return object[matchingKeys[0]!];
}

function readEvent(value: unknown, index: number): AuditEvent {
  const context = `event #${index + 1}`;

  if (!isObject(value)) {
    throw new AuditVerificationError("INVALID_EVENT", `${context}: expected an object`, {
      checkedEvents: index + 1,
      brokenAt: index + 1,
    });
  }

  const sequence = getField(value, "sequence", context);
  const previousHash = getField(value, "previousHash", context);
  const hash = getField(value, "hash", context);
  const canonicalData = getField(value, "canonicalData", context);

  if (!Number.isSafeInteger(sequence) || (sequence as number) < 1) {
    throw new AuditVerificationError(
      "INVALID_EVENT",
      `${context}: sequence must be a positive integer`,
      { checkedEvents: index + 1, brokenAt: index + 1 },
    );
  }

  if (typeof previousHash !== "string" || !SHA256_HEX.test(previousHash)) {
    throw new AuditVerificationError(
      "INVALID_EVENT",
      `${context}: previousHash must be a lowercase 64-character SHA-256 value`,
      { checkedEvents: index + 1, brokenAt: sequence as number },
    );
  }

  if (typeof hash !== "string" || !SHA256_HEX.test(hash)) {
    throw new AuditVerificationError(
      "INVALID_EVENT",
      `${context}: hash must be a lowercase 64-character SHA-256 value`,
      { checkedEvents: index + 1, brokenAt: sequence as number },
    );
  }

  if (typeof canonicalData !== "string") {
    throw new AuditVerificationError(
      "INVALID_EVENT",
      `${context}: canonicalData must be a string`,
      { checkedEvents: index + 1, brokenAt: sequence as number },
    );
  }

  return {
    sequence: sequence as number,
    previousHash,
    hash,
    canonicalData,
  };
}

function extractEvents(value: unknown): unknown[] {
  if (Array.isArray(value)) {
    return value;
  }

  if (!isObject(value)) {
    throw new AuditVerificationError(
      "INVALID_EXPORT",
      "export must be an event array or an object with events",
    );
  }

  const events = getField(value, "events", "export");
  if (!Array.isArray(events)) {
    throw new AuditVerificationError("INVALID_EXPORT", "export: events must be an array");
  }

  return events;
}

export function computeAuditHash(previousHash: string, canonicalData: string): string {
  return createHash("sha256")
    .update(`${previousHash}\n${canonicalData}`, "utf8")
    .digest("hex");
}

export function verifyAuditExport(
  value: unknown,
  target?: AuditVerificationTarget,
): VerificationResult {
  const rawEvents = extractEvents(value);
  let priorEvent: AuditEvent | null = null;

  if (
    target !== undefined &&
    (!Number.isSafeInteger(target.targetSequence) ||
      target.targetSequence < 0 ||
      !SHA256_HEX.test(target.targetHash))
  ) {
    throw new AuditVerificationError(
      "INVALID_TARGET",
      "target must contain a non-negative sequence and lowercase 64-character SHA-256 hash",
    );
  }

  for (let index = 0; index < rawEvents.length; index += 1) {
    const event = readEvent(rawEvents[index], index);
    const expectedSequence = index + 1;

    if (event.sequence !== expectedSequence) {
      throw new AuditVerificationError(
        "SEQUENCE_MISMATCH",
        `event #${index + 1}: expected sequence ${expectedSequence}, found ${event.sequence}`,
        { checkedEvents: index + 1, brokenAt: event.sequence },
      );
    }

    if (priorEvent === null && event.previousHash !== AUDIT_GENESIS_HASH) {
      throw new AuditVerificationError(
        "GENESIS_MISMATCH",
        `event #1: previousHash must equal the all-zero genesis hash`,
        { checkedEvents: 1, brokenAt: 1 },
      );
    }

    if (priorEvent !== null && event.previousHash !== priorEvent.hash) {
      throw new AuditVerificationError(
        "PREVIOUS_HASH_MISMATCH",
        `event #${index + 1}: previousHash does not match event #${index} hash`,
        { checkedEvents: index + 1, brokenAt: event.sequence },
      );
    }

    const expectedHash = computeAuditHash(event.previousHash, event.canonicalData);
    if (event.hash !== expectedHash) {
      throw new AuditVerificationError(
        "HASH_MISMATCH",
        `event #${index + 1}: hash mismatch (expected ${expectedHash}, found ${event.hash})`,
        { checkedEvents: index + 1, brokenAt: event.sequence },
      );
    }

    priorEvent = event;
  }

  if (target !== undefined && rawEvents.length !== target.targetSequence) {
    throw new AuditVerificationError(
      "TARGET_SEQUENCE_MISMATCH",
      `target sequence ${target.targetSequence} does not match verified event count ${rawEvents.length}`,
      {
        checkedEvents: rawEvents.length,
        brokenAt: Math.min(rawEvents.length + 1, target.targetSequence || rawEvents.length + 1),
      },
    );
  }

  const targetHead = priorEvent?.hash ?? AUDIT_GENESIS_HASH;
  if (target !== undefined && targetHead !== target.targetHash) {
    throw new AuditVerificationError(
      "TARGET_HASH_MISMATCH",
      `target hash does not match verified chain head`,
      { checkedEvents: rawEvents.length, brokenAt: rawEvents.length || null },
    );
  }

  return {
    count: rawEvents.length,
    chainHead: priorEvent?.hash ?? null,
  };
}

export function formatVerificationSummary(result: VerificationResult): string {
  if (result.count === 0) {
    return "OK: verified 0 audit events (empty chain)";
  }

  const noun = result.count === 1 ? "event" : "events";
  return `OK: verified ${result.count} audit ${noun}; chain head ${result.chainHead}`;
}
