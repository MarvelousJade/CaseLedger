import assert from "node:assert/strict";
import test from "node:test";
import type { AddressInfo } from "node:net";
import { closeServer, startHealthServer, type HealthState } from "../src/health.ts";
import type { Logger } from "../src/logger.ts";

const logger: Logger = { log() {} };

test("health endpoint reports dependency readiness without exposing configuration", async () => {
  const state: HealthState = {
    database: true,
    broker: false,
    brokerProvider: "rabbitmq",
  };
  const server = await startHealthServer(state, "127.0.0.1", 0, logger);
  const address = server.address() as AddressInfo;
  const url = `http://127.0.0.1:${address.port}/health`;

  try {
    const degraded = await fetch(url);
    assert.equal(degraded.status, 503);
    assert.deepEqual(await degraded.json(), {
      status: "degraded",
      database: true,
      broker: false,
      brokerProvider: "rabbitmq",
    });

    state.broker = true;
    const healthy = await fetch(url);
    assert.equal(healthy.status, 200);
    assert.deepEqual(await healthy.json(), {
      status: "healthy",
      database: true,
      broker: true,
      brokerProvider: "rabbitmq",
    });
  } finally {
    await closeServer(server);
  }
});
