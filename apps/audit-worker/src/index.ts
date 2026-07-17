import {
  connect,
  type ChannelModel,
  type ConsumeMessage,
  type RecoveringChannelModel,
} from "amqplib";
import { Pool } from "pg";
import { readConfig } from "./config.ts";
import { DeliveryHandler } from "./consumer.ts";
import { closeServer, startHealthServer, type HealthState } from "./health.ts";
import { createLogger, errorName } from "./logger.ts";
import { ResultOutboxPublisher } from "./outbox-publisher.ts";
import { VerificationProcessor } from "./processor.ts";
import { RabbitTransportPublisher } from "./rabbit-publisher.ts";
import { PostgresResultRepository } from "./repository.ts";
import { assertAuditTopology, REQUEST_QUEUE } from "./topology.ts";

async function main(): Promise<void> {
  const logger = createLogger();
  const config = readConfig();
  const health: HealthState = { database: false, rabbitmq: false };
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
  let connection: RecoveringChannelModel | undefined;
  let generation: AbortController | undefined;
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

    connection = await connect(config.amqpUrl, {
      recovery: {
        initialDelay: 250,
        maxDelay: 10_000,
        factor: 2,
        jitter: 0.2,
        async setup(model: ChannelModel) {
          generation?.abort();
          generation = new AbortController();
          const consumerChannel = await model.createChannel();
          const deliveryPublishingChannel = await model.createConfirmChannel();
          const outboxPublishingChannel = await model.createConfirmChannel();
          await assertAuditTopology(consumerChannel);
          await consumerChannel.prefetch(config.prefetch);

          const deliveryPublisher = new RabbitTransportPublisher(deliveryPublishingChannel);
          const handler = new DeliveryHandler(
            consumerChannel,
            deliveryPublisher,
            processor,
            logger,
            {
              maximumMessageBytes: config.maximumMessageBytes,
              workerVersion: config.workerVersion,
              onDatabaseStatus: (healthy) => {
                health.database = healthy;
              },
            },
          );

          await consumerChannel.consume(
            REQUEST_QUEUE,
            (message: ConsumeMessage | null) => {
              if (message === null) {
                health.rabbitmq = false;
                logger.log("warn", "audit_verification_consumer_cancelled");
                return;
              }

              void handler.handle(message).catch((error: unknown) => {
                health.rabbitmq = false;
                logger.log("error", "audit_verification_delivery_failed", {
                  errorType: errorName(error),
                });
                // The delivery remains unacknowledged; a broker disconnect or
                // process restart will safely redeliver it.
              });
            },
            { noAck: false },
          );

          const outboxPublisher = new RabbitTransportPublisher(outboxPublishingChannel);
          const outbox = new ResultOutboxPublisher(repository, outboxPublisher, logger, {
            batchSize: config.outboxBatchSize,
            leaseSeconds: config.outboxLeaseSeconds,
            pollMilliseconds: config.outboxPollMilliseconds,
            onDatabaseStatus: (healthy) => {
              health.database = healthy;
            },
          });
          void outbox.run(generation.signal).catch((error: unknown) => {
            logger.log("error", "audit_verification_outbox_stopped", {
              errorType: errorName(error),
            });
          });

          health.rabbitmq = true;
          logger.log("info", "audit_worker_rabbitmq_ready", {
            prefetch: config.prefetch,
          });
        },
      },
    });

    connection.on("disconnect", () => {
      health.rabbitmq = false;
      generation?.abort();
      logger.log("warn", "audit_worker_rabbitmq_disconnected");
    });
    connection.on("connect-failed", (error) => {
      health.rabbitmq = false;
      logger.log("warn", "audit_worker_rabbitmq_connect_failed", {
        errorType: errorName(error),
      });
    });

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

    logger.log("info", "audit_worker_started", { workerVersion: config.workerVersion });
    await waitForShutdownSignal();
    logger.log("info", "audit_worker_stopping");
  } catch (error) {
    logger.log("error", "audit_worker_startup_failed", { errorType: errorName(error) });
    process.exitCode = 1;
  } finally {
    if (healthTimer !== undefined) {
      clearInterval(healthTimer);
    }
    generation?.abort();
    health.database = false;
    health.rabbitmq = false;
    await connection?.close().catch(() => undefined);
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
