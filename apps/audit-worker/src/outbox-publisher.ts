import type { Logger } from "./logger.ts";
import type { VerificationResultMessage } from "./contracts.ts";
import type { PostgresResultRepository } from "./repository.ts";

export interface ResultPublisher {
  publishResult(result: VerificationResultMessage): Promise<void>;
}

export interface OutboxPublisherOptions {
  batchSize: number;
  leaseSeconds: number;
  pollMilliseconds: number;
  onDatabaseStatus?: (healthy: boolean) => void;
}

export class ResultOutboxPublisher {
  private readonly repository: PostgresResultRepository;
  private readonly publisher: ResultPublisher;
  private readonly logger: Logger;
  private readonly options: OutboxPublisherOptions;

  constructor(
    repository: PostgresResultRepository,
    publisher: ResultPublisher,
    logger: Logger,
    options: OutboxPublisherOptions,
  ) {
    this.repository = repository;
    this.publisher = publisher;
    this.logger = logger;
    this.options = options;
  }

  async run(signal: AbortSignal): Promise<void> {
    while (!signal.aborted) {
      try {
        const items = await this.repository.claimOutbox(
          this.options.batchSize,
          this.options.leaseSeconds,
        );
        this.options.onDatabaseStatus?.(true);

        for (const item of items) {
          if (signal.aborted) {
            return;
          }
          try {
            await this.publisher.publishResult(item.result);
            const marked = await this.repository.markPublished(item.resultId, item.lockId);
            this.options.onDatabaseStatus?.(true);
            this.logger.log(marked ? "info" : "warn", "audit_verification_result_published", {
              jobId: item.jobId,
              resultId: item.resultId,
              marked,
              publishAttempt: item.publishAttempts + 1,
            });
          } catch {
            await this.repository
              .releaseOutbox(item.resultId, item.lockId)
              .then(() => this.options.onDatabaseStatus?.(true))
              .catch(() => this.options.onDatabaseStatus?.(false));
            this.logger.log("warn", "audit_verification_result_publish_failed", {
              jobId: item.jobId,
              resultId: item.resultId,
              publishAttempt: item.publishAttempts + 1,
            });
          }
        }

        if (items.length === 0) {
          await wait(this.options.pollMilliseconds, signal);
        }
      } catch {
        this.options.onDatabaseStatus?.(false);
        this.logger.log("warn", "audit_verification_outbox_poll_failed");
        await wait(this.options.pollMilliseconds, signal);
      }
    }
  }
}

async function wait(milliseconds: number, signal: AbortSignal): Promise<void> {
  if (signal.aborted) {
    return;
  }

  await new Promise<void>((resolve) => {
    const timer = setTimeout(resolve, milliseconds);
    timer.unref();
    signal.addEventListener(
      "abort",
      () => {
        clearTimeout(timer);
        resolve();
      },
      { once: true },
    );
  });
}
