import { setTimeout as delay } from "node:timers/promises";
import type { AzureServiceBusBrokerConfig } from "./config.ts";
import type { BrokerHostContext, BrokerRuntime } from "./broker-host.ts";
import { VerificationDeliveryHandler } from "./delivery-handler.ts";
import { errorName } from "./logger.ts";
import { ResultOutboxPublisher } from "./outbox-publisher.ts";
import {
  AzureServiceBusResultPublisher,
  defaultServiceBusClientFactory,
  processServiceBusMessage,
  safeServiceBusErrorFields,
  type ServiceBusClientLike,
  type ServiceBusClientFactory,
} from "./azure-service-bus.ts";

export async function startAzureServiceBusHost(
  broker: AzureServiceBusBrokerConfig,
  context: BrokerHostContext,
  clientFactory: ServiceBusClientFactory = defaultServiceBusClientFactory,
  probeIntervalMilliseconds = 30_000,
): Promise<BrokerRuntime> {
  const client = clientFactory(broker);

  try {
    await probeAzureServiceBus(client, broker);
  } catch (error) {
    context.health.broker = false;
    context.logger.log("warn", "audit_worker_broker_probe_failed", {
      provider: broker.provider,
      errorType: errorName(error),
    });
    await Promise.allSettled([client.close()]);
    throw error;
  }

  const receiver = client.createReceiver(
    broker.topic,
    broker.requestSubscription,
    {
      receiveMode: "peekLock",
      maxAutoLockRenewalDurationInMs: 300_000,
      skipParsingBodyAsJson: true,
      identifier: "caseledger-audit-worker-requests",
    },
  );
  const topicSender = client.createSender(broker.topic);
  const outboxAbort = new AbortController();
  const probeAbort = new AbortController();
  const handler = new VerificationDeliveryHandler(context.processor, context.logger, {
    maximumMessageBytes: context.config.maximumMessageBytes,
    onDatabaseStatus: (healthy) => {
      context.health.database = healthy;
    },
  });
  const resultPublisher = new AzureServiceBusResultPublisher(topicSender, (healthy) => {
    context.health.broker = healthy;
  });
  const outbox = new ResultOutboxPublisher(
    context.repository,
    resultPublisher,
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
  void outbox.run(outboxAbort.signal).catch((error: unknown) => {
    context.logger.log("error", "audit_verification_outbox_stopped", {
      provider: broker.provider,
      errorType: errorName(error),
    });
  });

  const subscription = receiver.subscribe(
    {
      async processMessage(message) {
        await processServiceBusMessage(
          message,
          receiver,
          topicSender,
          handler,
          async (error) => {
            context.logger.log("error", "audit_verification_delivery_failed", {
              provider: broker.provider,
              errorType: errorName(error),
            });
          },
        );
        context.health.broker = true;
      },
      async processError(args) {
        context.health.broker = false;
        context.logger.log("warn", "audit_worker_broker_error", {
          provider: broker.provider,
          ...safeServiceBusErrorFields(args),
        });
      },
    },
    {
      autoCompleteMessages: false,
      maxConcurrentCalls: context.config.prefetch,
    },
  );

  context.health.broker = true;
  const probeTask = runBrokerProbeLoop(
    client,
    broker,
    context,
    probeIntervalMilliseconds,
    probeAbort.signal,
  );
  context.logger.log("info", "audit_worker_broker_ready", {
    provider: broker.provider,
    prefetch: context.config.prefetch,
  });

  return {
    async close() {
      outboxAbort.abort();
      probeAbort.abort();
      context.health.broker = false;
      await probeTask;
      await Promise.allSettled([
        subscription.close(),
        receiver.close(),
        topicSender.close(),
      ]);
      await client.close();
    },
  };
}

async function probeAzureServiceBus(
  client: ServiceBusClientLike,
  broker: AzureServiceBusBrokerConfig,
  abortSignal?: AbortSignal,
): Promise<void> {
  const receiver = client.createReceiver(
    broker.topic,
    broker.requestSubscription,
    {
      receiveMode: "peekLock",
      skipParsingBodyAsJson: true,
      identifier: "caseledger-audit-worker-probe",
    },
  );
  const sender = client.createSender(broker.topic);

  try {
    await Promise.all([
      receiver.peekMessages(1, abortSignal === undefined ? undefined : { abortSignal }),
      sender.createMessageBatch(
        abortSignal === undefined ? undefined : { abortSignal },
      ),
    ]);
  } finally {
    await Promise.allSettled([receiver.close(), sender.close()]);
  }
}

async function runBrokerProbeLoop(
  client: ServiceBusClientLike,
  broker: AzureServiceBusBrokerConfig,
  context: BrokerHostContext,
  intervalMilliseconds: number,
  abortSignal: AbortSignal,
): Promise<void> {
  while (!abortSignal.aborted) {
    try {
      await delay(intervalMilliseconds, undefined, { signal: abortSignal });
    } catch (error) {
      if (abortSignal.aborted || errorName(error) === "AbortError") {
        return;
      }
      throw error;
    }

    try {
      await probeAzureServiceBus(client, broker, abortSignal);
      context.health.broker = true;
    } catch (error) {
      if (abortSignal.aborted || errorName(error) === "AbortError") {
        return;
      }
      context.health.broker = false;
      context.logger.log("warn", "audit_worker_broker_probe_failed", {
        provider: broker.provider,
        errorType: errorName(error),
      });
    }
  }
}
