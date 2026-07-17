import { createHash } from "node:crypto";
import { z } from "zod";

export const REQUEST_MESSAGE_TYPE = "caseledger.audit.verification.requested.v1" as const;
export const RESULT_MESSAGE_TYPE = "caseledger.audit.verification.result.v1" as const;
export const VERIFICATION_PROFILE = "export-chain-v1" as const;

const sha256Schema = z.string().regex(/^[0-9a-f]{64}$/);

export const auditEventSchema = z
  .object({
    sequence: z.number().int().positive(),
    previousHash: sha256Schema,
    hash: sha256Schema,
    canonicalData: z.string(),
  })
  .strict();

export const verificationRequestSchema = z
  .object({
    schemaVersion: z.literal(1),
    messageType: z.literal(REQUEST_MESSAGE_TYPE),
    messageId: z.string().uuid(),
    jobId: z.string().uuid(),
    correlationId: z.string().min(1).max(128),
    requestedAt: z.string().datetime({ offset: true }),
    data: z
      .object({
        caseId: z.string().uuid(),
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
  for (const event of events) {
    digest.update(`${event.sequence}\n${event.previousHash}\n${event.hash}\n`, "utf8");
    const canonicalBytes = Buffer.from(event.canonicalData, "utf8");
    digest.update(`${canonicalBytes.byteLength}\n`, "utf8");
    digest.update(canonicalBytes);
    digest.update("\n", "utf8");
  }

  return digest.digest("hex");
}

export function resultIdFor(jobId: string): string {
  return `audit-verification:${jobId}:v1`;
}
