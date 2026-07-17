import {
  MessageContractError,
  parseVerificationRequest,
  type VerificationRequest,
} from "./contracts.ts";
import type { Logger } from "./logger.ts";
import type { VerificationProcessor } from "./processor.ts";
import { IdempotencyConflictError } from "./repository.ts";
import { retryTierForAttempt, type RetryTier } from "./topology.ts";

export interface BrokerDelivery {
  content: Buffer;
  attempt: number;
  complete(): Promise<void>;
  deadLetter(code: string): Promise<void>;
  scheduleRetry(
    request: VerificationRequest,
    tier: RetryTier,
    nextAttempt: number,
  ): Promise<void>;
}

export interface VerificationDeliveryHandlerOptions {
  maximumMessageBytes: number;
  onDatabaseStatus?: (healthy: boolean) => void;
}

export class VerificationDeliveryHandler {
  private readonly processor: VerificationProcessor;
  private readonly logger: Logger;
  private readonly maximumMessageBytes: number;
  private readonly onDatabaseStatus: (healthy: boolean) => void;

  constructor(
    processor: VerificationProcessor,
    logger: Logger,
    options: VerificationDeliveryHandlerOptions,
  ) {
    this.processor = processor;
    this.logger = logger;
    this.maximumMessageBytes = options.maximumMessageBytes;
    this.onDatabaseStatus = options.onDatabaseStatus ?? (() => undefined);
  }

  async handle(delivery: BrokerDelivery): Promise<void> {
    let request: VerificationRequest;
    try {
      request = parseVerificationRequest(delivery.content, this.maximumMessageBytes);
    } catch (error) {
      const code = error instanceof MessageContractError ? error.code : "SCHEMA_INVALID";
      await delivery.deadLetter(code);
      this.logger.log("warn", "audit_verification_message_dead_lettered", {
        code,
        attempt: delivery.attempt,
      });
      return;
    }

    try {
      const stored = await this.processor.process(request, delivery.attempt);
      this.onDatabaseStatus(true);
      await delivery.complete();
      this.logger.log("info", "audit_verification_processed", {
        jobId: request.jobId,
        caseId: request.data.caseId,
        outcome: stored.result.outcome,
        disposition: stored.disposition,
        attempt: delivery.attempt,
      });
    } catch (error) {
      if (error instanceof IdempotencyConflictError) {
        this.onDatabaseStatus(true);
        await delivery.deadLetter("IDEMPOTENCY_KEY_REUSED");
        this.logger.log("warn", "audit_verification_idempotency_conflict", {
          jobId: request.jobId,
          caseId: request.data.caseId,
          attempt: delivery.attempt,
        });
        return;
      }

      this.onDatabaseStatus(false);
      const retryTier = retryTierForAttempt(delivery.attempt);
      if (retryTier !== null) {
        await delivery.scheduleRetry(request, retryTier, delivery.attempt + 1);
        await delivery.complete();
        this.logger.log("warn", "audit_verification_retry_scheduled", {
          jobId: request.jobId,
          caseId: request.data.caseId,
          attempt: delivery.attempt + 1,
          delayMilliseconds: retryTier.delayMilliseconds,
        });
        return;
      }

      await this.processor.storeTerminalError(
        request,
        delivery.attempt,
        "RETRY_EXHAUSTED",
      );
      this.onDatabaseStatus(true);
      await delivery.deadLetter("RETRY_EXHAUSTED");
      this.logger.log("error", "audit_verification_retry_exhausted", {
        jobId: request.jobId,
        caseId: request.data.caseId,
        attempt: delivery.attempt,
      });
    }
  }
}
