import { createHash } from "node:crypto";
import { z } from "zod";

export const REQUEST_MESSAGE_TYPE = "caseledger.audit.verification.requested.v1" as const;
export const RESULT_MESSAGE_TYPE = "caseledger.audit.verification.result.v1" as const;
export const VERIFICATION_PROFILE = "export-chain-v1" as const;

const sha256Schema = z.string().regex(/^[0-9a-f]{64}$/);
const lowercaseGuidSchema = z
  .string()
  .regex(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
const utcRoundTripTimestampSchema = z
  .string()
  .regex(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$/);

export const auditEventSchema = z
  .object({
    eventId: lowercaseGuidSchema,
    caseId: lowercaseGuidSchema,
    sequence: z.number().int().positive(),
    eventType: z.string().min(1).max(80),
    description: z.string().min(1).max(500),
    actorId: lowercaseGuidSchema,
    actorName: z.string().min(1).max(100),
    createdAt: utcRoundTripTimestampSchema,
    previousHash: sha256Schema,
    hash: sha256Schema,
    canonicalData: z.string().max(65_536),
  })
  .strict();

export const verificationRequestSchema = z
  .object({
    schemaVersion: z.literal(1),
    messageType: z.literal(REQUEST_MESSAGE_TYPE),
    messageId: lowercaseGuidSchema,
    jobId: lowercaseGuidSchema,
    correlationId: z.string().min(1).max(128),
    requestedAt: z.string().datetime({ offset: true }),
    data: z
      .object({
        caseId: lowercaseGuidSchema,
        verificationProfile: z.literal(VERIFICATION_PROFILE),
        targetSequence: z.number().int().nonnegative(),
        targetHash: sha256Schema,
        snapshot: z
          .object({
            eventCount: z.number().int().nonnegative(),
            events: z.array(auditEventSchema),
          })
          .strict(),
      })
      .strict(),
  })
  .strict()
  .superRefine((request, context) => {
    if (request.messageId !== request.jobId) {
      context.addIssue({
        code: "custom",
        path: ["messageId"],
        message: "messageId must equal jobId",
      });
    }
    request.data.snapshot.events.forEach((event, index) => {
      if (event.caseId !== request.data.caseId) {
        context.addIssue({
          code: "custom",
          path: ["data", "snapshot", "events", index, "caseId"],
          message: "event caseId must equal request caseId",
        });
      }
    });
  });

export type VerificationRequest = z.infer<typeof verificationRequestSchema>;
export type AuditSnapshotEvent = z.infer<typeof auditEventSchema>;

export type ResultOutcome = "valid" | "invalid" | "error";

export interface ResultError {
  code: string;
  message: string;
  retryable: false;
}

export interface VerificationResultMessage {
  schemaVersion: 1;
  messageType: typeof RESULT_MESSAGE_TYPE;
  resultId: string;
  jobId: string;
  correlationId: string;
  caseId: string;
  verificationProfile: typeof VERIFICATION_PROFILE;
  snapshotSha256: string;
  completedAt: string;
  outcome: ResultOutcome;
  checkedEvents: number;
  chainHead: string | null;
  brokenAt: number | null;
  error: ResultError | null;
  attempt: number;
  workerVersion: string;
}

export type ContractErrorCode = "MESSAGE_TOO_LARGE" | "INVALID_JSON" | "SCHEMA_INVALID";

export class MessageContractError extends Error {
  readonly code: ContractErrorCode;

  constructor(code: ContractErrorCode, message: string) {
    super(message);
    this.name = "MessageContractError";
    this.code = code;
  }
}

export function parseVerificationRequest(
  content: Buffer,
  maximumBytes: number,
): VerificationRequest {
  if (content.byteLength > maximumBytes) {
    throw new MessageContractError(
      "MESSAGE_TOO_LARGE",
      `message exceeds the ${maximumBytes}-byte limit`,
    );
  }

  let value: unknown;
  try {
    value = JSON.parse(content.toString("utf8"));
  } catch {
    throw new MessageContractError("INVALID_JSON", "message body is not valid JSON");
  }

  const parsed = verificationRequestSchema.safeParse(value);
  if (!parsed.success) {
    const firstIssue = parsed.error.issues[0];
    const location = firstIssue?.path.join(".") || "message";
    throw new MessageContractError(
      "SCHEMA_INVALID",
      `${location}: ${firstIssue?.message ?? "invalid request schema"}`,
    );
  }

  return parsed.data;
}

export function computeSnapshotSha256(events: readonly AuditSnapshotEvent[]): string {
  const digest = createHash("sha256");
  const orderedEvents = [...events].sort((left, right) => left.sequence - right.sequence);
  for (const event of orderedEvents) {
    const fields = [
      event.eventId,
      event.caseId,
      String(event.sequence),
      event.eventType,
      event.description,
      event.actorId,
      event.actorName,
      event.createdAt,
      event.previousHash,
      event.hash,
      event.canonicalData,
    ];
    for (const field of fields) {
      appendFramed(digest, field);
    }
  }

  return digest.digest("hex");
}

export function computeRequestFingerprint(request: VerificationRequest): string {
  const digest = createHash("sha256");
  const requestFields = [
    "caseledger.audit.verification.request-fingerprint.v1",
    String(request.schemaVersion),
    request.messageType,
    request.messageId,
    request.jobId,
    request.correlationId,
    request.requestedAt,
    request.data.caseId,
    request.data.verificationProfile,
    String(request.data.targetSequence),
    request.data.targetHash,
    String(request.data.snapshot.eventCount),
    String(request.data.snapshot.events.length),
  ];
  for (const field of requestFields) {
    appendFramed(digest, field);
  }

  for (const event of request.data.snapshot.events) {
    const eventFields = [
      event.eventId,
      event.caseId,
      String(event.sequence),
      event.eventType,
      event.description,
      event.actorId,
      event.actorName,
      event.createdAt,
      event.previousHash,
      event.hash,
      event.canonicalData,
    ];
    for (const field of eventFields) {
      appendFramed(digest, field);
    }
  }

  return digest.digest("hex");
}

export function resultIdFor(jobId: string): string {
  return `audit-verification:${jobId}:v1`;
}

function appendFramed(digest: ReturnType<typeof createHash>, value: string): void {
  const bytes = Buffer.from(value, "utf8");
  digest.update(`${bytes.byteLength}\n`, "utf8");
  digest.update(bytes);
  digest.update("\n", "utf8");
}
