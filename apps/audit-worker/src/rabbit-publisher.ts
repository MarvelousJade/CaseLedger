import { once } from "node:events";
import type { ConfirmChannel, ConsumeMessage, Options } from "amqplib";
import {
  REQUEST_MESSAGE_TYPE,
  RESULT_MESSAGE_TYPE,
  type VerificationRequest,
  type VerificationResultMessage,
} from "./contracts.ts";
import {
  AUDIT_EXCHANGE,
  DEAD_EXCHANGE,
  DEAD_ROUTING_KEY,
  RESULT_ROUTING_KEY,
  RETRY_EXCHANGE,
  type RetryTier,
} from "./topology.ts";

export interface TransportPublisher {
  publishRetry(
    message: ConsumeMessage,
    request: VerificationRequest,
    tier: RetryTier,
    nextAttempt: number,
  ): Promise<void>;
  publishDeadLetter(
    message: ConsumeMessage,
    code: string,
    attempt: number,
  ): Promise<void>;
  publishResult(result: VerificationResultMessage): Promise<void>;
}

export class RabbitTransportPublisher implements TransportPublisher {
  private readonly channel: ConfirmChannel;

  constructor(channel: ConfirmChannel) {
    this.channel = channel;
  }

  async publishRetry(
    message: ConsumeMessage,
    request: VerificationRequest,
    tier: RetryTier,
    nextAttempt: number,
  ): Promise<void> {
    await publishConfirmed(
      this.channel,
      RETRY_EXCHANGE,
      tier.routingKey,
      message.content,
      requestProperties(request, message, nextAttempt),
    );
  }

  async publishDeadLetter(
    message: ConsumeMessage,
    code: string,
    attempt: number,
  ): Promise<void> {
    const headers = safeHeaders(message.properties.headers);
    headers["x-caseledger-attempt"] = attempt;
    headers["x-caseledger-error-code"] = truncate(code, 80);

    const messageId = safeString(message.properties.messageId);
    const correlationId = safeString(message.properties.correlationId);
    await publishConfirmed(this.channel, DEAD_EXCHANGE, DEAD_ROUTING_KEY, message.content, {
      contentType: safeString(message.properties.contentType) ?? "application/json",
      contentEncoding: "utf-8",
      deliveryMode: 2,
      persistent: true,
      ...(messageId === undefined ? {} : { messageId }),
      ...(correlationId === undefined ? {} : { correlationId }),
      timestamp: Math.floor(Date.now() / 1_000),
      type: safeString(message.properties.type) ?? REQUEST_MESSAGE_TYPE,
      appId: "caseledger-audit-worker",
      headers,
    });
  }

  async publishResult(result: VerificationResultMessage): Promise<void> {
    await publishConfirmed(
      this.channel,
      AUDIT_EXCHANGE,
      RESULT_ROUTING_KEY,
      Buffer.from(JSON.stringify(result), "utf8"),
      {
        contentType: "application/json",
        contentEncoding: "utf-8",
        deliveryMode: 2,
        persistent: true,
        messageId: result.resultId,
        correlationId: result.correlationId,
        timestamp: Math.floor(Date.parse(result.completedAt) / 1_000),
        type: RESULT_MESSAGE_TYPE,
        appId: "caseledger-audit-worker",
        headers: {
          "x-caseledger-schema-version": 1,
          "x-caseledger-attempt": result.attempt,
        },
      },
    );
  }
}

export async function publishConfirmed(
  channel: ConfirmChannel,
  exchange: string,
  routingKey: string,
  content: Buffer,
  options: Options.Publish,
): Promise<void> {
  const writable = channel.publish(exchange, routingKey, content, options);
  if (!writable) {
    await once(channel, "drain");
  }
  await channel.waitForConfirms();
}

function requestProperties(
  request: VerificationRequest,
  original: ConsumeMessage,
  attempt: number,
): Options.Publish {
  const headers = safeHeaders(original.properties.headers);
  headers["x-caseledger-schema-version"] = 1;
  headers["x-caseledger-attempt"] = attempt;

  return {
    contentType: "application/json",
    contentEncoding: "utf-8",
    deliveryMode: 2,
    persistent: true,
    messageId: request.jobId,
    correlationId: request.correlationId,
    timestamp: Math.floor(Date.now() / 1_000),
    type: REQUEST_MESSAGE_TYPE,
    appId: "caseledger-audit-worker",
    headers,
  };
}

function safeHeaders(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return {};
  }
  return { ...(value as Record<string, unknown>) };
}

function safeString(value: unknown): string | undefined {
  return typeof value === "string" && value.length <= 256 ? value : undefined;
}

function truncate(value: string, maximumLength: number): string {
  return value.length <= maximumLength ? value : value.slice(0, maximumLength);
}
