import {
  AuditVerificationError,
  verifyAuditProjection,
  type AuditVerificationErrorCode,
} from "@caseledger/audit-verifier";
import {
  RESULT_MESSAGE_TYPE,
  computeRequestFingerprint,
  computeSnapshotSha256,
  resultIdFor,
  type VerificationRequest,
  type VerificationResultMessage,
} from "./contracts.ts";
import type { ResultRepository, StoredResult } from "./repository.ts";

export interface ProcessorOptions {
  workerVersion: string;
  clock?: () => Date;
}

export class VerificationProcessor {
  private readonly repository: ResultRepository;
  private readonly workerVersion: string;
  private readonly clock: () => Date;

  constructor(repository: ResultRepository, options: ProcessorOptions) {
    this.repository = repository;
    this.workerVersion = options.workerVersion;
    this.clock = options.clock ?? (() => new Date());
  }

  async process(request: VerificationRequest, attempt: number): Promise<StoredResult> {
    const requestFingerprint = computeRequestFingerprint(request);
    const snapshotSha256 = computeSnapshotSha256(request.data.snapshot.events);
    const result = buildVerificationResult(
      request,
      snapshotSha256,
      attempt,
      this.workerVersion,
      this.clock,
    );
    return this.repository.storeResult(
      request.jobId,
      requestFingerprint,
      snapshotSha256,
      result,
    );
  }

  async storeTerminalError(
    request: VerificationRequest,
    attempt: number,
    code: "RETRY_EXHAUSTED",
  ): Promise<StoredResult> {
    const requestFingerprint = computeRequestFingerprint(request);
    const snapshotSha256 = computeSnapshotSha256(request.data.snapshot.events);
    const result = buildTerminalErrorResult(
      request,
      attempt,
      this.workerVersion,
      code,
      this.clock,
    );
    return this.repository.storeResult(
      request.jobId,
      requestFingerprint,
      snapshotSha256,
      result,
    );
  }
}

export function buildVerificationResult(
  request: VerificationRequest,
  snapshotSha256: string,
  attempt: number,
  workerVersion: string,
  clock: () => Date = () => new Date(),
): VerificationResultMessage {
  const base = resultBase(request, snapshotSha256, attempt, workerVersion, clock);

  try {
    const verified = verifyAuditProjection(request.data.snapshot.events, {
      targetSequence: request.data.targetSequence,
      targetHash: request.data.targetHash,
    });

    if (request.data.snapshot.eventCount !== verified.count) {
      return {
        ...base,
        outcome: "invalid",
        checkedEvents: verified.count,
        chainHead: null,
        brokenAt: null,
        error: {
          code: "SNAPSHOT_EVENT_COUNT_MISMATCH",
          message: "declared snapshot event count does not match the verified event count",
          retryable: false,
        },
      };
    }

    return {
      ...base,
      outcome: "valid",
      checkedEvents: verified.count,
      chainHead: verified.chainHead,
      brokenAt: null,
      error: null,
    };
  } catch (error) {
    if (!(error instanceof AuditVerificationError)) {
      throw error;
    }

    return {
      ...base,
      outcome: "invalid",
      checkedEvents: error.checkedEvents,
      chainHead: null,
      brokenAt: error.brokenAt,
      error: {
        code: error.code,
        message: publicVerificationMessage(error.code),
        retryable: false,
      },
    };
  }
}

export function buildTerminalErrorResult(
  request: VerificationRequest,
  attempt: number,
  workerVersion: string,
  code: "IDEMPOTENCY_KEY_REUSED" | "RETRY_EXHAUSTED",
  clock: () => Date = () => new Date(),
): VerificationResultMessage {
  const snapshotSha256 = computeSnapshotSha256(request.data.snapshot.events);
  return {
    ...resultBase(request, snapshotSha256, attempt, workerVersion, clock),
    outcome: "error",
    checkedEvents: 0,
    chainHead: null,
    brokenAt: null,
    error: {
      code,
      message:
        code === "IDEMPOTENCY_KEY_REUSED"
          ? "jobId was reused with different immutable request content"
          : "verification could not complete after the retry policy was exhausted",
      retryable: false,
    },
  };
}

function resultBase(
  request: VerificationRequest,
  snapshotSha256: string,
  attempt: number,
  workerVersion: string,
  clock: () => Date,
): Pick<
  VerificationResultMessage,
  | "schemaVersion"
  | "messageType"
  | "resultId"
  | "jobId"
  | "correlationId"
  | "caseId"
  | "verificationProfile"
  | "snapshotSha256"
  | "completedAt"
  | "attempt"
  | "workerVersion"
> {
  return {
    schemaVersion: 1,
    messageType: RESULT_MESSAGE_TYPE,
    resultId: resultIdFor(request.jobId),
    jobId: request.jobId,
    correlationId: request.correlationId,
    caseId: request.data.caseId,
    verificationProfile: request.data.verificationProfile,
    snapshotSha256,
    completedAt: clock().toISOString(),
    attempt,
    workerVersion,
  };
}

function publicVerificationMessage(code: AuditVerificationErrorCode): string {
  const messages: Record<AuditVerificationErrorCode, string> = {
    INVALID_EXPORT: "audit snapshot envelope is invalid",
    INVALID_EVENT: "audit snapshot contains a malformed event",
    INVALID_TARGET: "audit verification target is invalid",
    GENESIS_MISMATCH: "first event does not reference the required genesis hash",
    SEQUENCE_MISMATCH: "audit event sequence is not contiguous",
    PREVIOUS_HASH_MISMATCH: "audit event does not reference the preceding event hash",
    HASH_MISMATCH: "audit event hash does not match its canonical content",
    TARGET_SEQUENCE_MISMATCH: "verified event count does not match the target sequence",
    TARGET_HASH_MISMATCH: "verified chain head does not match the target hash",
    CANONICAL_PROJECTION_MISMATCH:
      "canonical audit data does not match the projected event fields",
  };
  return messages[code];
}
