import type { ConsumeMessage } from "amqplib";
import type { VerificationRequest } from "./contracts.ts";
import { VerificationDeliveryHandler } from "./delivery-handler.ts";
import type { Logger } from "./logger.ts";
import type { VerificationProcessor } from "./processor.ts";
import type { TransportPublisher } from "./rabbit-publisher.ts";

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
  private readonly handler: VerificationDeliveryHandler;

  constructor(
    acknowledger: Acknowledger,
    publisher: TransportPublisher,
    processor: VerificationProcessor,
    logger: Logger,
    options: DeliveryHandlerOptions,
  ) {
    this.acknowledger = acknowledger;
    this.publisher = publisher;
    this.handler = new VerificationDeliveryHandler(processor, logger, {
      maximumMessageBytes: options.maximumMessageBytes,
      ...(options.onDatabaseStatus === undefined
        ? {}
        : { onDatabaseStatus: options.onDatabaseStatus }),
    });
  }

  async handle(message: ConsumeMessage): Promise<void> {
    const attempt = readAttempt(message);
    await this.handler.handle({
      content: message.content,
      attempt,
      complete: async () => this.acknowledger.ack(message),
      deadLetter: async (code) => {
        await this.publisher.publishDeadLetter(message, code, attempt);
        this.acknowledger.ack(message);
      },
      scheduleRetry: async (
        request: VerificationRequest,
        tier,
        nextAttempt,
      ) => this.publisher.publishRetry(message, request, tier, nextAttempt),
    });
  }
}

export function readAttempt(message: ConsumeMessage): number {
  const value = message.properties.headers?.["x-caseledger-attempt"];
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0
    ? value
    : 0;
}
