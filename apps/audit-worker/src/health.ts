import { createServer, type Server } from "node:http";
import type { Logger } from "./logger.ts";

export interface HealthState {
  database: boolean;
  broker: boolean;
  brokerProvider: "rabbitmq" | "azure-service-bus";
}

export async function startHealthServer(
  state: HealthState,
  host: string,
  port: number,
  logger: Logger,
): Promise<Server> {
  const server = createServer((request, response) => {
    if (request.method !== "GET" || request.url !== "/health") {
      response.writeHead(404).end();
      return;
    }

    const healthy = state.database && state.broker;
    response.writeHead(healthy ? 200 : 503, {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
    });
    response.end(
      JSON.stringify({
        status: healthy ? "healthy" : "degraded",
        database: state.database,
        broker: state.broker,
        brokerProvider: state.brokerProvider,
      }),
    );
  });

  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, host, () => {
      server.off("error", reject);
      resolve();
    });
  });
  logger.log("info", "health_server_started", { host, port });
  return server;
}

export async function closeServer(server: Server): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    server.close((error) => (error === undefined ? resolve() : reject(error)));
  });
}
