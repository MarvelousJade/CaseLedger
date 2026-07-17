import {
  connect,
  type ChannelModel,
  type ConsumeMessage,
} from "amqplib";
import type { BrokerHostContext, BrokerRuntime } from "./broker-host.ts";
import type { RabbitMqBrokerConfig } from "./config.ts";
import { DeliveryHandler } from "./consumer.ts";
import { errorName } from "./logger.ts";
import { ResultOutboxPublisher } from "./outbox-publisher.ts";
import { RabbitTransportPublisher } from "./rabbit-publisher.ts";
import { assertAuditTopology, REQUEST_QUEUE } from "./topology.ts";

export async function startRabbitMqHost(
  broker: RabbitMqBrokerConfig,
  context: BrokerHostContext,
): Promise<BrokerRuntime> {
  let generation: AbortController | undefined;
  const connection = await connect(broker.amqpUrl, {
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
        await consumerChannel.prefetch(context.config.prefetch);

        const deliveryPublisher = new RabbitTransportPublisher(deliveryPublishingChannel);
        const handler = new DeliveryHandler(
          consumerChannel,
          deliveryPublisher,
          context.processor,
          context.logger,
          {
            maximumMessageBytes: context.config.maximumMessageBytes,
            workerVersion: context.config.workerVersion,
            onDatabaseStatus: (healthy) => {
              context.health.database = healthy;
            },
          },
        );

        await consumerChannel.consume(
          REQUEST_QUEUE,
          (message: ConsumeMessage | null) => {
            if (message === null) {
              context.health.broker = false;
              context.logger.log("warn", "audit_verification_consumer_cancelled");
              return;
            }

            void handler.handle(message).catch((error: unknown) => {
              context.health.broker = false;
              context.logger.log("error", "audit_verification_delivery_failed", {
                provider: broker.provider,
                errorType: errorName(error),
              });
              // The delivery remains unacknowledged and will be redelivered
              // after broker recovery or process restart.
            });
          },
          { noAck: false },
        );

        const outboxPublisher = new RabbitTransportPublisher(outboxPublishingChannel);
        const outbox = new ResultOutboxPublisher(
          context.repository,
          outboxPublisher,
          context.logger,
          {
            batchSize: context.config.outboxBatchSize,
            leaseSeconds: context.config.outboxLeaseSeconds,
            pollMilliseconds: context.config.outboxPollMilliseconds,
            onDatabaseStatus: (healthy) => {
              context.health.database = healthy;
            },
          },
        );
        void outbox.run(generation.signal).catch((error: unknown) => {
          context.logger.log("error", "audit_verification_outbox_stopped", {
            provider: broker.provider,
            errorType: errorName(error),
          });
        });

        context.health.broker = true;
        context.logger.log("info", "audit_worker_broker_ready", {
          provider: broker.provider,
          prefetch: context.config.prefetch,
        });
      },
    },
  });

  connection.on("disconnect", () => {
    context.health.broker = false;
    generation?.abort();
    context.logger.log("warn", "audit_worker_broker_disconnected", {
      provider: broker.provider,
    });
  });
  connection.on("connect-failed", (error) => {
    context.health.broker = false;
    context.logger.log("warn", "audit_worker_broker_connect_failed", {
      provider: broker.provider,
      errorType: errorName(error),
    });
  });

  return {
    async close() {
      generation?.abort();
      context.health.broker = false;
      await connection.close();
    },
  };
}
