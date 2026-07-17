import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import type { Pool, PoolClient, QueryResultRow } from "pg";
import type { VerificationResultMessage } from "./contracts.ts";
import { verificationResultSchema } from "./result-schema.ts";
import { RESULT_ROUTING_KEY } from "./topology.ts";

export class IdempotencyConflictError extends Error {
  readonly jobId: string;

  constructor(jobId: string) {
    super("jobId was already used with a different snapshot digest");
    this.name = "IdempotencyConflictError";
    this.jobId = jobId;
  }
}

export interface StoredResult {
  disposition: "stored" | "cached";
  result: VerificationResultMessage;
}

export interface OutboxItem {
  resultId: string;
  jobId: string;
  routingKey: string;
  result: VerificationResultMessage;
  lockId: string;
  publishAttempts: number;
}

export interface ResultRepository {
  storeResult(
    jobId: string,
    snapshotSha256: string,
    result: VerificationResultMessage,
  ): Promise<StoredResult>;
}

interface InboxRow extends QueryResultRow {
  snapshot_sha256: string;
  result_payload: unknown;
}

interface InsertedInboxRow extends QueryResultRow {
  job_id: string;
}

interface OutboxRow extends QueryResultRow {
  result_id: string;
  job_id: string;
  routing_key: string;
  result_payload: unknown;
  lock_id: string;
  publish_attempts: number;
}

export class PostgresResultRepository implements ResultRepository {
  private readonly pool: Pool;

  constructor(pool: Pool) {
    this.pool = pool;
  }

  async migrate(): Promise<void> {
    const migration = await readFile(
      new URL("../migrations/001_inbox_outbox.sql", import.meta.url),
      "utf8",
    );
    await this.pool.query(migration);
  }

  async ping(): Promise<void> {
    await this.pool.query("SELECT 1");
  }

  async storeResult(
    jobId: string,
    snapshotSha256: string,
    result: VerificationResultMessage,
  ): Promise<StoredResult> {
    const client = await this.pool.connect();
    try {
      await client.query("BEGIN");
      const inserted = await client.query<InsertedInboxRow>(
        `INSERT INTO audit_worker.inbox
           (job_id, snapshot_sha256, result_id, result_payload, completed_at)
         VALUES ($1, $2, $3, $4::jsonb, $5::timestamptz)
         ON CONFLICT (job_id) DO NOTHING
         RETURNING job_id`,
        [jobId, snapshotSha256, result.resultId, JSON.stringify(result), result.completedAt],
      );

      if (inserted.rows[0] !== undefined) {
        await this.ensureOutbox(client, result);
        await client.query("COMMIT");
        return { disposition: "stored", result };
      }

      const existing = await client.query<InboxRow>(
        `SELECT snapshot_sha256, result_payload
           FROM audit_worker.inbox
          WHERE job_id = $1
          FOR UPDATE`,
        [jobId],
      );
      const row = existing.rows[0];
      if (row === undefined) {
        throw new Error("idempotency row disappeared during result storage");
      }
      if (row.snapshot_sha256 !== snapshotSha256) {
        throw new IdempotencyConflictError(jobId);
      }

      const cached = verificationResultSchema.parse(row.result_payload);
      await this.ensureOutbox(client, cached);
      await client.query("COMMIT");
      return { disposition: "cached", result: cached };
    } catch (error) {
      await rollback(client);
      throw error;
    } finally {
      client.release();
    }
  }

  async claimOutbox(limit: number, leaseSeconds: number): Promise<OutboxItem[]> {
    const lockId = randomUUID();
    const result = await this.pool.query<OutboxRow>(
      `WITH candidates AS (
         SELECT result_id
           FROM audit_worker.result_outbox
          WHERE published_at IS NULL
            AND next_attempt_at <= now()
            AND (locked_until IS NULL OR locked_until < now())
          ORDER BY created_at
          LIMIT $1
          FOR UPDATE SKIP LOCKED
       )
       UPDATE audit_worker.result_outbox AS item
          SET lock_id = $2,
              locked_until = now() + ($3 * interval '1 second')
         FROM candidates
        WHERE item.result_id = candidates.result_id
      RETURNING item.result_id, item.job_id, item.routing_key,
                item.result_payload, item.lock_id, item.publish_attempts`,
      [limit, lockId, leaseSeconds],
    );

    return result.rows.map((row) => ({
      resultId: row.result_id,
      jobId: row.job_id,
      routingKey: row.routing_key,
      result: verificationResultSchema.parse(row.result_payload),
      lockId: row.lock_id,
      publishAttempts: row.publish_attempts,
    }));
  }

  async markPublished(resultId: string, lockId: string): Promise<boolean> {
    const result = await this.pool.query(
      `UPDATE audit_worker.result_outbox
          SET published_at = now(),
              publish_attempts = publish_attempts + 1,
              lock_id = NULL,
              locked_until = NULL
        WHERE result_id = $1
          AND lock_id = $2
          AND published_at IS NULL`,
      [resultId, lockId],
    );
    return result.rowCount === 1;
  }

  async releaseOutbox(resultId: string, lockId: string): Promise<void> {
    await this.pool.query(
      `UPDATE audit_worker.result_outbox
          SET publish_attempts = publish_attempts + 1,
              next_attempt_at = now() +
                  (LEAST(300, POWER(2, LEAST(publish_attempts, 8))) * interval '1 second'),
              lock_id = NULL,
              locked_until = NULL
        WHERE result_id = $1
          AND lock_id = $2
          AND published_at IS NULL`,
      [resultId, lockId],
    );
  }

  private async ensureOutbox(
    client: PoolClient,
    result: VerificationResultMessage,
  ): Promise<void> {
    await client.query(
      `INSERT INTO audit_worker.result_outbox
         (result_id, job_id, routing_key, result_payload)
       VALUES ($1, $2, $3, $4::jsonb)
       ON CONFLICT (result_id) DO NOTHING`,
      [result.resultId, result.jobId, RESULT_ROUTING_KEY, JSON.stringify(result)],
    );
  }
}

async function rollback(client: PoolClient): Promise<void> {
  try {
    await client.query("ROLLBACK");
  } catch {
    // The original transaction error is more actionable than rollback failure.
  }
}
