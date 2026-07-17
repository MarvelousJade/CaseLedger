import { Pool } from "pg";
import { startAzureServiceBusHost } from "./azure-service-bus-host.ts";
import type { BrokerRuntime } from "./broker-host.ts";
import { readConfig } from "./config.ts";
import { closeServer, startHealthServer, type HealthState } from "./health.ts";
import { createLogger, errorName } from "./logger.ts";
import { VerificationProcessor } from "./processor.ts";
import { startRabbitMqHost } from "./rabbit-host.ts";
import { PostgresResultRepository } from "./repository.ts";

async function main(): Promise<void> {
  const logger = createLogger();
  const config = readConfig();
  const health: HealthState = {
    database: false,
    broker: false,
    brokerProvider: config.broker.provider,
  };
  const server = await startHealthServer(
    health,
    config.healthHost,
    config.healthPort,
    logger,
  );
  const pool = new Pool({
    connectionString: config.databaseUrl,
    application_name: "caseledger-audit-worker",
    max: 10,
  });
  let brokerRuntime: BrokerRuntime | undefined;
  let healthTimer: NodeJS.Timeout | undefined;

  try {
    const repository = new PostgresResultRepository(pool);
    await repository.migrate();
    await repository.ping();
    health.database = true;
    logger.log("info", "audit_worker_database_ready");

    const processor = new VerificationProcessor(repository, {
      workerVersion: config.workerVersion,
    });

    const hostContext = { config, repository, processor, logger, health };
    brokerRuntime =
      config.broker.provider === "rabbitmq"
        ? await startRabbitMqHost(config.broker, hostContext)
        : await startAzureServiceBusHost(config.broker, hostContext);

    healthTimer = setInterval(() => {
      void repository
        .ping()
        .then(() => {
          health.database = true;
        })
        .catch(() => {
          health.database = false;
        });
    }, 5_000);
    healthTimer.unref();

    logger.log("info", "audit_worker_started", {
      workerVersion: config.workerVersion,
      provider: config.broker.provider,
    });
    await waitForShutdownSignal();
    logger.log("info", "audit_worker_stopping");
  } catch (error) {
    logger.log("error", "audit_worker_startup_failed", { errorType: errorName(error) });
    process.exitCode = 1;
  } finally {
    if (healthTimer !== undefined) {
      clearInterval(healthTimer);
    }
    health.database = false;
    health.broker = false;
    await brokerRuntime?.close().catch(() => undefined);
    await pool.end().catch(() => undefined);
    await closeServer(server).catch(() => undefined);
  }
}

async function waitForShutdownSignal(): Promise<void> {
  await new Promise<void>((resolve) => {
    const stop = () => resolve();
    process.once("SIGINT", stop);
    process.once("SIGTERM", stop);
  });
}

await main().catch((error: unknown) => {
  createLogger().log("error", "audit_worker_startup_failed", {
    errorType: errorName(error),
  });
  process.exitCode = 1;
});
