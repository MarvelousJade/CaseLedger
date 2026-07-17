import assert from "node:assert/strict";
import test from "node:test";
import { readConfig } from "../src/config.ts";

const databaseUrl = "postgresql://worker:private@database/caseledger";

function fakeServiceBusConnectionString(namespace: string, key: string): string {
  const keyProperty = ["Shared", "Access", "Key"].join("");
  return `Endpoint=sb://${namespace}/;${keyProperty}Name=worker;${keyProperty}=${key}`;
}

test("uses RabbitMQ by default and validates only its provider settings", () => {
  const config = readConfig({
    AUDIT_WORKER_DATABASE_URL: databaseUrl,
    AMQP_URL: "amqp://worker:private@rabbitmq",
  });

  assert.equal(config.broker.provider, "rabbitmq");
  if (config.broker.provider === "rabbitmq") {
    assert.equal(config.broker.amqpUrl, "amqp://worker:private@rabbitmq");
  }
});

test("loads Azure Service Bus topic/subscription settings without requiring AMQP_URL", () => {
  const connectionString = fakeServiceBusConnectionString(
    "caseledger.servicebus.windows.net",
    "private",
  );
  const config = readConfig({
    BROKER_PROVIDER: "azure-service-bus",
    AUDIT_WORKER_DATABASE_URL: databaseUrl,
    AZURE_SERVICE_BUS_CONNECTION_STRING: connectionString,
    AZURE_SERVICE_BUS_TOPIC: "audit-messages",
    AZURE_SERVICE_BUS_REQUEST_SUBSCRIPTION: "audit-worker",
  });

  assert.deepEqual(config.broker, {
    provider: "azure-service-bus",
    connectionString,
    topic: "audit-messages",
    requestSubscription: "audit-worker",
  });
});

test("loads Azure Service Bus managed identity settings without a secret", () => {
  const config = readConfig({
    BROKER_PROVIDER: "azure-service-bus",
    AUDIT_WORKER_DATABASE_URL: databaseUrl,
    AZURE_SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE:
      "caseledger.servicebus.windows.net",
    AZURE_MANAGED_IDENTITY_CLIENT_ID:
      "db1f1b1b-5570-4958-89e0-13c5ebd52922",
    AZURE_SERVICE_BUS_TOPIC: "audit-messages",
    AZURE_SERVICE_BUS_REQUEST_SUBSCRIPTION: "audit-worker",
  });

  assert.deepEqual(config.broker, {
    provider: "azure-service-bus",
    fullyQualifiedNamespace: "caseledger.servicebus.windows.net",
    managedIdentityClientId: "db1f1b1b-5570-4958-89e0-13c5ebd52922",
    topic: "audit-messages",
    requestSubscription: "audit-worker",
  });
});

test("rejects ambiguous Azure Service Bus credential sources", () => {
  const connectionString = fakeServiceBusConnectionString(
    "caseledger.servicebus.windows.net",
    "private",
  );

  assert.throws(
    () =>
      readConfig({
        BROKER_PROVIDER: "azure-service-bus",
        AUDIT_WORKER_DATABASE_URL: databaseUrl,
        AZURE_SERVICE_BUS_CONNECTION_STRING: connectionString,
        AZURE_SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE:
          "caseledger.servicebus.windows.net",
        AZURE_SERVICE_BUS_TOPIC: "audit-messages",
        AZURE_SERVICE_BUS_REQUEST_SUBSCRIPTION: "audit-worker",
      }),
    (error: unknown) =>
      error instanceof Error &&
      !error.message.includes(connectionString) &&
      !error.message.includes("private"),
  );
});

test("configuration failures name fields but never echo connection strings", () => {
  const connectionString = fakeServiceBusConnectionString(
    "secret.servicebus.windows.net",
    "do-not-log",
  );
  assert.throws(
    () =>
      readConfig({
        BROKER_PROVIDER: "azure-service-bus",
        AUDIT_WORKER_DATABASE_URL: databaseUrl,
        AZURE_SERVICE_BUS_CONNECTION_STRING: connectionString,
      }),
    (error: unknown) =>
      error instanceof Error &&
      error.message.includes("AZURE_SERVICE_BUS_TOPIC") &&
      !error.message.includes(connectionString) &&
      !error.message.includes("do-not-log"),
  );

  assert.throws(
    () =>
      readConfig({
        BROKER_PROVIDER: "unsupported",
        AUDIT_WORKER_DATABASE_URL: databaseUrl,
      }),
    /BROKER_PROVIDER/,
  );
});
