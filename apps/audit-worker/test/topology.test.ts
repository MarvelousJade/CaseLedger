import assert from "node:assert/strict";
import test from "node:test";
import type { Options } from "amqplib";
import {
  AUDIT_EXCHANGE,
  DEAD_EXCHANGE,
  DEAD_QUEUE,
  REQUEST_QUEUE,
  RETRY_EXCHANGE,
  RETRY_TIERS,
  assertAuditTopology,
  type TopologyChannel,
} from "../src/topology.ts";

test("declares durable request, retry, and dead-letter topology", async () => {
  const exchanges: Array<[string, string, Options.AssertExchange | undefined]> = [];
  const queues: Array<[string, Options.AssertQueue | undefined]> = [];
  const bindings: Array<[string, string, string]> = [];
  const channel: TopologyChannel = {
    async assertExchange(name, type, options) {
      exchanges.push([name, type, options]);
      return {} as never;
    },
    async assertQueue(name, options) {
      queues.push([name, options]);
      return {} as never;
    },
    async bindQueue(queue, source, pattern) {
      bindings.push([queue, source, pattern]);
      return {} as never;
    },
  };

  await assertAuditTopology(channel);

  assert.deepEqual(
    exchanges.map(([name, type, options]) => [name, type, options?.durable]),
    [
      [AUDIT_EXCHANGE, "topic", true],
      [RETRY_EXCHANGE, "direct", true],
      [DEAD_EXCHANGE, "direct", true],
    ],
  );
  assert.ok(queues.some(([name]) => name === REQUEST_QUEUE));
  assert.ok(queues.some(([name]) => name === DEAD_QUEUE));
  for (const tier of RETRY_TIERS) {
    const declared = queues.find(([name]) => name === tier.queue);
    assert.equal(declared?.[1]?.arguments?.["x-message-ttl"], tier.delayMilliseconds);
    assert.equal(declared?.[1]?.arguments?.["x-dead-letter-exchange"], AUDIT_EXCHANGE);
  }
  assert.equal(bindings.length, RETRY_TIERS.length + 2);
});
