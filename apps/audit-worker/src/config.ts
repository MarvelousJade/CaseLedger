import { z } from "zod";

const positiveInteger = z.coerce.number().int().positive();

const environmentSchema = z.object({
  AUDIT_WORKER_DATABASE_URL: z.string().min(1),
  HEALTH_HOST: z.string().min(1).default("0.0.0.0"),
  HEALTH_PORT: positiveInteger.max(65_535).default(8081),
  RABBITMQ_PREFETCH: positiveInteger.default(8),
  MAX_MESSAGE_BYTES: positiveInteger.default(1_048_576),
  OUTBOX_POLL_MILLISECONDS: positiveInteger.default(500),
  OUTBOX_BATCH_SIZE: positiveInteger.max(100).default(20),
  OUTBOX_LEASE_SECONDS: positiveInteger.default(30),
  WORKER_VERSION: z.string().min(1).max(64).default("1.0.0"),
});

const rabbitEnvironmentSchema = z.object({
  AMQP_URL: z.string().min(1),
});

const serviceBusEnvironmentSchema = z.object({
  AZURE_SERVICE_BUS_CONNECTION_STRING: z.string().min(1).optional(),
  AZURE_SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE: z.string().min(1).max(255).optional(),
  AZURE_MANAGED_IDENTITY_CLIENT_ID: z.uuid().optional(),
  AZURE_SERVICE_BUS_TOPIC: z.string().min(1).max(260),
  AZURE_SERVICE_BUS_REQUEST_SUBSCRIPTION: z.string().min(1).max(50),
});

export interface RabbitMqBrokerConfig {
  provider: "rabbitmq";
  amqpUrl: string;
}

interface AzureServiceBusBrokerConfigBase {
  provider: "azure-service-bus";
  topic: string;
  requestSubscription: string;
}

export type AzureServiceBusBrokerConfig = AzureServiceBusBrokerConfigBase & (
  | {
      connectionString: string;
      fullyQualifiedNamespace?: never;
      managedIdentityClientId?: never;
    }
  | {
      connectionString?: never;
      fullyQualifiedNamespace: string;
      managedIdentityClientId?: string;
    }
);

export type BrokerConfig = RabbitMqBrokerConfig | AzureServiceBusBrokerConfig;

export interface WorkerConfig {
  broker: BrokerConfig;
  databaseUrl: string;
  healthHost: string;
  healthPort: number;
  prefetch: number;
  maximumMessageBytes: number;
  outboxPollMilliseconds: number;
  outboxBatchSize: number;
  outboxLeaseSeconds: number;
  workerVersion: string;
}

export function readConfig(environment: NodeJS.ProcessEnv = process.env): WorkerConfig {
  const provider = environment.BROKER_PROVIDER ?? "rabbitmq";
  if (provider !== "rabbitmq" && provider !== "azure-service-bus") {
    throw new Error("invalid worker configuration: BROKER_PROVIDER");
  }

  const parsed = environmentSchema.safeParse(environment);
  if (!parsed.success) {
    const fields = [...new Set(parsed.error.issues.map((issue) => issue.path.join(".")))];
    throw new Error(`invalid worker configuration: ${fields.join(", ")}`);
  }

  const broker = readBrokerConfig(provider, environment);
  return {
    broker,
    databaseUrl: parsed.data.AUDIT_WORKER_DATABASE_URL,
    healthHost: parsed.data.HEALTH_HOST,
    healthPort: parsed.data.HEALTH_PORT,
    prefetch: parsed.data.RABBITMQ_PREFETCH,
    maximumMessageBytes: parsed.data.MAX_MESSAGE_BYTES,
    outboxPollMilliseconds: parsed.data.OUTBOX_POLL_MILLISECONDS,
    outboxBatchSize: parsed.data.OUTBOX_BATCH_SIZE,
    outboxLeaseSeconds: parsed.data.OUTBOX_LEASE_SECONDS,
    workerVersion: parsed.data.WORKER_VERSION,
  };
}

function readBrokerConfig(
  provider: BrokerConfig["provider"],
  environment: NodeJS.ProcessEnv,
): BrokerConfig {
  if (provider === "rabbitmq") {
    const parsed = rabbitEnvironmentSchema.safeParse(environment);
    if (!parsed.success) {
      throw configurationError(parsed.error);
    }
    return { provider, amqpUrl: parsed.data.AMQP_URL };
  }

  const parsed = serviceBusEnvironmentSchema.safeParse(environment);
  if (!parsed.success) {
    throw configurationError(parsed.error);
  }

  const connectionString = parsed.data.AZURE_SERVICE_BUS_CONNECTION_STRING;
  const fullyQualifiedNamespace =
    parsed.data.AZURE_SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE;
  if ((connectionString === undefined) === (fullyQualifiedNamespace === undefined)) {
    throw new Error(
      "invalid worker configuration: " +
        "AZURE_SERVICE_BUS_CONNECTION_STRING, " +
        "AZURE_SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE",
    );
  }

  const common = {
    provider,
    topic: parsed.data.AZURE_SERVICE_BUS_TOPIC,
    requestSubscription: parsed.data.AZURE_SERVICE_BUS_REQUEST_SUBSCRIPTION,
  } as const;
  if (connectionString !== undefined) {
    if (parsed.data.AZURE_MANAGED_IDENTITY_CLIENT_ID !== undefined) {
      throw new Error(
        "invalid worker configuration: AZURE_MANAGED_IDENTITY_CLIENT_ID",
      );
    }
    return { ...common, connectionString };
  }

  if (!isFullyQualifiedNamespace(fullyQualifiedNamespace!)) {
    throw new Error(
      "invalid worker configuration: " +
        "AZURE_SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE",
    );
  }
  const managedIdentityClientId =
    parsed.data.AZURE_MANAGED_IDENTITY_CLIENT_ID;
  return managedIdentityClientId === undefined
    ? { ...common, fullyQualifiedNamespace: fullyQualifiedNamespace! }
    : {
        ...common,
        fullyQualifiedNamespace: fullyQualifiedNamespace!,
        managedIdentityClientId,
      };
}

function isFullyQualifiedNamespace(value: string): boolean {
  try {
    const url = new URL(`https://${value}`);
    return (
      url.hostname === value.toLowerCase() &&
      url.pathname === "/" &&
      !value.includes(":") &&
      value.includes(".")
    );
  } catch {
    return false;
  }
}

function configurationError(error: z.ZodError): Error {
  const fields = [...new Set(error.issues.map((issue) => issue.path.join(".")))];
  return new Error(`invalid worker configuration: ${fields.join(", ")}`);
}
