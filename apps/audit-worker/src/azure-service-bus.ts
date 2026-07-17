import {
  ServiceBusClient,
  type ProcessErrorArgs,
  type ServiceBusMessage,
  type ServiceBusReceivedMessage,
  type ServiceBusReceiver,
  type ServiceBusReceiverOptions,
  type ServiceBusSender,
} from "@azure/service-bus";
import type { BrokerDelivery, VerificationDeliveryHandler } from "./delivery-handler.ts";
import {
  REQUEST_MESSAGE_TYPE,
  RESULT_MESSAGE_TYPE,
  type VerificationRequest,
  type VerificationResultMessage,
} from "./contracts.ts";
import type { ResultPublisher } from "./outbox-publisher.ts";
import type { RetryTier } from "./topology.ts";

type SettlementReceiver = Pick<
  ServiceBusReceiver,
  "completeMessage" | "deadLetterMessage"
>;
type SchedulingSender = Pick<ServiceBusSender, "scheduleMessages">;
type SendingSender = Pick<ServiceBusSender, "sendMessages">;

export class AzureServiceBusDelivery implements BrokerDelivery {
  readonly content: Buffer;
  readonly attempt: number;
  private readonly message: ServiceBusReceivedMessage;
  private readonly receiver: SettlementReceiver;
  private readonly requestSender: SchedulingSender;
  private readonly clock: () => Date;

  constructor(
    message: ServiceBusReceivedMessage,
    receiver: SettlementReceiver,
    requestSender: SchedulingSender,
    clock: () => Date = () => new Date(),
  ) {
    this.message = message;
    this.receiver = receiver;
    this.requestSender = requestSender;
    this.clock = clock;
    this.content = serviceBusBodyToBuffer(message.body);
    this.attempt = readServiceBusAttempt(message);
  }

  async complete(): Promise<void> {
    await this.receiver.completeMessage(this.message);
  }

  async deadLetter(code: string): Promise<void> {
    await this.receiver.deadLetterMessage(this.message, {
      deadLetterReason: safeDeadLetterReason(code),
      deadLetterErrorDescription: "CaseLedger rejected the audit verification message",
    });
  }

  async scheduleRetry(
    request: VerificationRequest,
    tier: RetryTier,
    nextAttempt: number,
  ): Promise<void> {
    const applicationProperties: NonNullable<ServiceBusMessage["applicationProperties"]> = {
      "x-caseledger-schema-version": 1,
      "x-caseledger-attempt": nextAttempt,
    };
    const traceparent = this.message.applicationProperties?.traceparent;
    if (typeof traceparent === "string" && traceparent.length <= 256) {
      applicationProperties.traceparent = traceparent;
    }

    await this.requestSender.scheduleMessages(
      {
        body: this.content,
        messageId: `${request.jobId}:retry:${nextAttempt}`,
        correlationId: request.correlationId,
        contentType: "application/json",
        subject: REQUEST_MESSAGE_TYPE,
        applicationProperties,
      },
      new Date(this.clock().getTime() + tier.delayMilliseconds),
    );
  }
}

export class AzureServiceBusResultPublisher implements ResultPublisher {
  private readonly sender: SendingSender;
  private readonly onStatus: (healthy: boolean) => void;

  constructor(sender: SendingSender, onStatus: (healthy: boolean) => void = () => undefined) {
    this.sender = sender;
    this.onStatus = onStatus;
  }

  async publishResult(result: VerificationResultMessage): Promise<void> {
    try {
      await this.sender.sendMessages({
        body: Buffer.from(JSON.stringify(result), "utf8"),
        messageId: result.resultId,
        correlationId: result.correlationId,
        contentType: "application/json",
        subject: RESULT_MESSAGE_TYPE,
        applicationProperties: {
          "x-caseledger-schema-version": 1,
          "x-caseledger-attempt": result.attempt,
        },
      });
      this.onStatus(true);
    } catch (error) {
      this.onStatus(false);
      throw error;
    }
  }
}

export interface ServiceBusClientLike {
  createReceiver(
    topicName: string,
    subscriptionName: string,
    options?: ServiceBusReceiverOptions,
  ): ServiceBusReceiver;
  createSender(topicName: string): ServiceBusSender;
  close(): Promise<void>;
}

export type ServiceBusClientFactory = (connectionString: string) => ServiceBusClientLike;

export function defaultServiceBusClientFactory(connectionString: string): ServiceBusClientLike {
  return new ServiceBusClient(connectionString, {
    identifier: "caseledger-audit-worker",
  });
}

export function serviceBusBodyToBuffer(value: unknown): Buffer {
  if (Buffer.isBuffer(value)) {
    return value;
  }
  if (value instanceof Uint8Array) {
    return Buffer.from(value);
  }
  if (typeof value === "string") {
    return Buffer.from(value, "utf8");
  }
  return Buffer.from(JSON.stringify(value), "utf8");
}

export function readServiceBusAttempt(message: ServiceBusReceivedMessage): number {
  const value = message.applicationProperties?.["x-caseledger-attempt"];
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0
    ? value
    : 0;
}

export async function processServiceBusMessage(
  message: ServiceBusReceivedMessage,
  receiver: ServiceBusReceiver,
  requestSender: ServiceBusSender,
  handler: VerificationDeliveryHandler,
  onError: (error: unknown) => Promise<void>,
): Promise<void> {
  try {
    await handler.handle(new AzureServiceBusDelivery(message, receiver, requestSender));
  } catch (error) {
    await onError(error);
    await receiver.abandonMessage(message);
  }
}

export function safeServiceBusErrorFields(args: ProcessErrorArgs): {
  errorSource: ProcessErrorArgs["errorSource"];
  errorType: string;
} {
  return {
    errorSource: args.errorSource,
    errorType: args.error.name,
  };
}

export function safeDeadLetterReason(value: string): string {
  const normalized = value.toUpperCase().replace(/[^A-Z0-9_]/g, "_").slice(0, 80);
  return normalized.length === 0 ? "UNSPECIFIED" : normalized;
}
