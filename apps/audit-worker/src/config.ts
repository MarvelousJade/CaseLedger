import { z } from "zod";

const positiveInteger = z.coerce.number().int().positive();

const environmentSchema = z.object({
  AMQP_URL: z.string().min(1),
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

export interface WorkerConfig {
  amqpUrl: string;
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
  const parsed = environmentSchema.safeParse(environment);
  if (!parsed.success) {
    const fields = [...new Set(parsed.error.issues.map((issue) => issue.path.join(".")))];
    throw new Error(`invalid worker configuration: ${fields.join(", ")}`);
  }

  return {
    amqpUrl: parsed.data.AMQP_URL,
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
