import type { WorkerConfig } from "./config.ts";
import type { HealthState } from "./health.ts";
import type { Logger } from "./logger.ts";
import type { VerificationProcessor } from "./processor.ts";
import type { PostgresResultRepository } from "./repository.ts";

export interface BrokerHostContext {
  config: WorkerConfig;
  repository: PostgresResultRepository;
  processor: VerificationProcessor;
  logger: Logger;
  health: HealthState;
}

export interface BrokerRuntime {
  close(): Promise<void>;
}
