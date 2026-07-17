import type { ConsumeMessage } from "amqplib";
import {
  MessageContractError,
  parseVerificationRequest,
  type VerificationRequest,
} from "./contracts.ts";
import type { Logger } from "./logger.ts";
import { buildTerminalErrorResult, type VerificationProcessor } from "./processor.ts";
import type { TransportPublisher } from "./rabbit-publisher.ts";
import { IdempotencyConflictError } from "./repository.ts";
import { retryTierForAttempt } from "./topology.ts";

export interface Acknowledger {
  ack(message: ConsumeMessage): void;
}

export interface DeliveryHandlerOptions {
  maximumMessageBytes: number;
  workerVersion: string;
  clock?: () => Date;
  onDatabaseStatus?: (healthy: boolean) => void;
}

export class DeliveryHandler {
  private readonly acknowledger: Acknowledger;
  private readonly publisher: TransportPublisher;
  private readonly processor: VerificationProcessor;
  private readonly logger: Logger;
  private readonly maximumMessageBytes: number;
  private readonly workerVersion: string;
  private readonly clock: () => Date;
  private readonly onDatabaseStatus: (healthy: boolean) => void;

  constructor(
    acknowledger: Acknowledger,
    publisher: TransportPublisher,
    processor: VerificationProcessor,
    logger: Logger,
    options: DeliveryHandlerOptions,
  ) {
    this.acknowledger = acknowledger;
    this.publisher = publisher;
    this.processor = processor;
    this.logger = logger;
    this.maximumMessageBytes = options.maximumMessageBytes;
    this.workerVersion = options.workerVersion;
    this.clock = options.clock ?? (() => new Date());
    this.onDatabaseStatus = options.onDatabaseStatus ?? (() => undefined);
  }

  async handle(message: ConsumeMessage): Promise<void> {
    const attempt = readAttempt(message);
    let request: VerificationRequest;

    try {
      request = parseVerificationRequest(message.content, this.maximumMessageBytes);
    } catch (error) {
      const code = error instanceof MessageContractError ? error.code : "SCHEMA_INVALID";
      await this.publisher.publishDeadLetter(message, code, attempt);
      this.acknowledger.ack(message);
      this.logger.log("warn", "audit_verification_message_dead_lettered", {
        code,
        attempt,
      });
      return;
    }

    try {
      const stored = await this.processor.process(request, attempt);
      this.onDatabaseStatus(true);
      this.acknowledger.ack(message);
      this.logger.log("info", "audit_verification_processed", {
        jobId: request.jobId,
        caseId: request.data.caseId,
        outcome: stored.result.outcome,
        disposition: stored.disposition,
        attempt,
      });
    } catch (error) {
      if (error instanceof IdempotencyConflictError) {
        this.onDatabaseStatus(true);
        await this.publisher.publishDeadLetter(
          message,
          "IDEMPOTENCY_KEY_REUSED",
          attempt,
        );
        this.acknowledger.ack(message);
        this.logger.log("warn", "audit_verification_idempotency_conflict", {
          jobId: request.jobId,
          caseId: request.data.caseId,
          attempt,
        });
        return;
      }

      this.onDatabaseStatus(false);
      const retryTier = retryTierForAttempt(attempt);
      if (retryTier !== null) {
        await this.publisher.publishRetry(message, request, retryTier, attempt + 1);
        this.acknowledger.ack(message);
        this.logger.log("warn", "audit_verification_retry_scheduled", {
          jobId: request.jobId,
          caseId: request.data.caseId,
          attempt: attempt + 1,
          delayMilliseconds: retryTier.delayMilliseconds,
        });
        return;
      }

      const terminal = buildTerminalErrorResult(
        request,
        attempt,
        this.workerVersion,
        "RETRY_EXHAUSTED",
        this.clock,
      );
      await this.publisher.publishResult(terminal);
      await this.publisher.publishDeadLetter(message, "RETRY_EXHAUSTED", attempt);
      this.acknowledger.ack(message);
      this.logger.log("error", "audit_verification_retry_exhausted", {
        jobId: request.jobId,
        caseId: request.data.caseId,
        attempt,
      });
    }
  }
}

export function readAttempt(message: ConsumeMessage): number {
  const value = message.properties.headers?.["x-caseledger-attempt"];
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0
    ? value
    : 0;
}
