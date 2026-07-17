# CaseLedger audit worker

Node.js 24 service that consumes immutable audit snapshots, verifies them with
`@caseledger/audit-verifier`, and publishes versioned terminal results through a
durable PostgreSQL inbox/outbox.

## Configuration

Required environment variables:

- `AMQP_URL`: RabbitMQ connection URI.
- `AUDIT_WORKER_DATABASE_URL`: PostgreSQL connection URI. The worker creates and
  uses only the `audit_worker` schema.

Optional variables are `HEALTH_HOST` (`0.0.0.0`), `HEALTH_PORT` (`8081`),
`RABBITMQ_PREFETCH` (`8`), `MAX_MESSAGE_BYTES` (`1048576`),
`OUTBOX_POLL_MILLISECONDS` (`500`), `OUTBOX_BATCH_SIZE` (`20`),
`OUTBOX_LEASE_SECONDS` (`30`), and `WORKER_VERSION` (`1.0.0`). Values in
connection URIs and message bodies are never logged.

`GET /health` returns `200` only while PostgreSQL and RabbitMQ are ready, and
otherwise returns `503`.

## Delivery behavior

- Request handling uses manual acknowledgements. A request is acknowledged only
  after its terminal result is committed to the inbox and result outbox.
- A duplicate `jobId` with the same framed snapshot digest reuses the cached
  result. Reusing it for different content is sent to the dead-letter queue.
- Infrastructure failures retry after 5 seconds, 30 seconds, and 5 minutes.
  Exhaustion publishes a deterministic error result and then a dead-letter copy.
- Invalid audit chains are terminal domain results and are not retried.
- Result publishing uses a separate confirm channel and an outbox lease. A crash
  after publish but before marking the row can publish a duplicate; consumers
  must deduplicate the deterministic `resultId`.

## Development

```powershell
npm install
npm run typecheck
npm test
```
