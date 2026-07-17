export type LogLevel = "debug" | "info" | "warn" | "error";
export type LogFields = Record<string, boolean | number | string | null | undefined>;

export interface Logger {
  log(level: LogLevel, event: string, fields?: LogFields): void;
}

const FORBIDDEN_FIELD = /(body|canonical|password|payload|secret|token|url)/i;

export function createLogger(clock: () => Date = () => new Date()): Logger {
  return {
    log(level, event, fields = {}) {
      const safeFields = Object.fromEntries(
        Object.entries(fields).filter(
          ([key, value]) => value !== undefined && !FORBIDDEN_FIELD.test(key),
        ),
      );
      const line = JSON.stringify({
        timestamp: clock().toISOString(),
        level,
        event,
        ...safeFields,
      });

      if (level === "error") {
        console.error(line);
      } else if (level === "warn") {
        console.warn(line);
      } else {
        console.log(line);
      }
    },
  };
}

export function errorName(error: unknown): string {
  return error instanceof Error ? error.name : "UnknownError";
}
