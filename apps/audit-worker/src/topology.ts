import type { Channel, Options, Replies } from "amqplib";

export const AUDIT_EXCHANGE = "caseledger.audit";
export const RETRY_EXCHANGE = "caseledger.audit.retry";
export const DEAD_EXCHANGE = "caseledger.audit.dlx";

export const REQUEST_QUEUE = "caseledger.audit.verify.v1";
export const REQUEST_ROUTING_KEY = "audit.verification.requested.v1";
export const RESULT_ROUTING_KEY = "audit.verification.result.v1";
export const DEAD_QUEUE = "caseledger.audit.verify.dead.v1";
export const DEAD_ROUTING_KEY = "audit.verification.dead.v1";

export interface RetryTier {
  queue: string;
  routingKey: string;
  delayMilliseconds: number;
}

export const RETRY_TIERS: readonly RetryTier[] = [
  {
    queue: "caseledger.audit.verify.retry.5s.v1",
    routingKey: "audit.verification.retry.5s.v1",
    delayMilliseconds: 5_000,
  },
  {
    queue: "caseledger.audit.verify.retry.30s.v1",
    routingKey: "audit.verification.retry.30s.v1",
    delayMilliseconds: 30_000,
  },
  {
    queue: "caseledger.audit.verify.retry.5m.v1",
    routingKey: "audit.verification.retry.5m.v1",
    delayMilliseconds: 300_000,
  },
];

export interface TopologyChannel {
  assertExchange(
    exchange: string,
    type: string,
    options?: Options.AssertExchange,
  ): Promise<Replies.AssertExchange>;
  assertQueue(queue: string, options?: Options.AssertQueue): Promise<Replies.AssertQueue>;
  bindQueue(
    queue: string,
    source: string,
    pattern: string,
    args?: unknown,
  ): Promise<Replies.Empty>;
}

export async function assertAuditTopology(channel: TopologyChannel): Promise<void> {
  await channel.assertExchange(AUDIT_EXCHANGE, "topic", { durable: true });
  await channel.assertExchange(RETRY_EXCHANGE, "direct", { durable: true });
  await channel.assertExchange(DEAD_EXCHANGE, "direct", { durable: true });

  await channel.assertQueue(REQUEST_QUEUE, {
    durable: true,
    arguments: {
      "x-dead-letter-exchange": DEAD_EXCHANGE,
      "x-dead-letter-routing-key": DEAD_ROUTING_KEY,
    },
  });
  await channel.bindQueue(REQUEST_QUEUE, AUDIT_EXCHANGE, REQUEST_ROUTING_KEY);

  for (const tier of RETRY_TIERS) {
    await channel.assertQueue(tier.queue, {
      durable: true,
      arguments: {
        "x-message-ttl": tier.delayMilliseconds,
        "x-dead-letter-exchange": AUDIT_EXCHANGE,
        "x-dead-letter-routing-key": REQUEST_ROUTING_KEY,
      },
    });
    await channel.bindQueue(tier.queue, RETRY_EXCHANGE, tier.routingKey);
  }

  await channel.assertQueue(DEAD_QUEUE, { durable: true });
  await channel.bindQueue(DEAD_QUEUE, DEAD_EXCHANGE, DEAD_ROUTING_KEY);
}

export function retryTierForAttempt(attempt: number): RetryTier | null {
  return RETRY_TIERS[attempt] ?? null;
}

export function asTopologyChannel(channel: Channel): TopologyChannel {
  return channel;
}
