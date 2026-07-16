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

export class AuditVerificationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "AuditVerificationError";
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
    throw new AuditVerificationError(`${context}: missing ${fieldName}`);
  }

  if (matchingKeys.length > 1) {
    throw new AuditVerificationError(`${context}: ambiguous ${fieldName} fields`);
  }

  return object[matchingKeys[0]];
}

function readEvent(value: unknown, index: number): AuditEvent {
  const context = `event #${index + 1}`;

  if (!isObject(value)) {
    throw new AuditVerificationError(`${context}: expected an object`);
  }

  const sequence = getField(value, "sequence", context);
  const previousHash = getField(value, "previousHash", context);
  const hash = getField(value, "hash", context);
  const canonicalData = getField(value, "canonicalData", context);

  if (!Number.isSafeInteger(sequence) || (sequence as number) < 1) {
    throw new AuditVerificationError(`${context}: sequence must be a positive integer`);
  }

  if (typeof previousHash !== "string") {
    throw new AuditVerificationError(`${context}: previousHash must be a string`);
  }

  if (typeof hash !== "string" || !SHA256_HEX.test(hash)) {
    throw new AuditVerificationError(
      `${context}: hash must be a lowercase 64-character SHA-256 value`,
    );
  }

  if (typeof canonicalData !== "string") {
    throw new AuditVerificationError(`${context}: canonicalData must be a string`);
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
    throw new AuditVerificationError("export must be an event array or an object with events");
  }

  const events = getField(value, "events", "export");
  if (!Array.isArray(events)) {
    throw new AuditVerificationError("export: events must be an array");
  }

  return events;
}

export function computeAuditHash(previousHash: string, canonicalData: string): string {
  return createHash("sha256")
    .update(`${previousHash}\n${canonicalData}`, "utf8")
    .digest("hex");
}

export function verifyAuditExport(value: unknown): VerificationResult {
  const rawEvents = extractEvents(value);
  let priorEvent: AuditEvent | null = null;

  for (let index = 0; index < rawEvents.length; index += 1) {
    const event = readEvent(rawEvents[index], index);
    const expectedSequence = index + 1;

    if (event.sequence !== expectedSequence) {
      throw new AuditVerificationError(
        `event #${index + 1}: expected sequence ${expectedSequence}, found ${event.sequence}`,
      );
    }

    if (priorEvent !== null && event.previousHash !== priorEvent.hash) {
      throw new AuditVerificationError(
        `event #${index + 1}: previousHash does not match event #${index} hash`,
      );
    }

    const expectedHash = computeAuditHash(event.previousHash, event.canonicalData);
    if (event.hash !== expectedHash) {
      throw new AuditVerificationError(
        `event #${index + 1}: hash mismatch (expected ${expectedHash}, found ${event.hash})`,
      );
    }

    priorEvent = event;
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
