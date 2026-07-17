# Local operations

This guide covers development, the distributed Compose demonstration, E2E verification, and the
configuration boundary for Azure Service Bus. It is not a production operations runbook.

## Fast verification

From the repository root:

```powershell
npm ci
npm ci --prefix apps/web
npm ci --prefix apps/audit-worker
npm run check
```

Useful focused commands:

```powershell
npm test --prefix apps/web
dotnet test CaseLedger.slnx
npm test --prefix apps/audit-worker
npm test --prefix tools/audit-verifier
npm test --prefix tools/webhook-receiver
```

`npm run check` runs frontend lint/build/tests, the .NET build and API suite, worker type checking and
tests, the independent verifier tests, and webhook-receiver tests.

## Lightweight development

```powershell
npm run setup
npm run dev
```

Endpoints:

- React development server: `http://localhost:5173`
- API process health: `http://localhost:5150/health`
- Swagger UI: `http://localhost:5150/swagger`
- OpenAPI JSON: `http://localhost:5150/swagger/v1/swagger.json`

This mode uses SQLite. Messaging and webhooks are disabled by default, so the queue endpoint returns
`503` and the React client falls back to synchronous audit verification. Swagger uses the same
cookie session as the client; call the login endpoint before protected routes.

## Distributed Compose demonstration

Copy the local-only environment template, replace every placeholder with a local value, and keep the
resulting `.env` file untracked:

```powershell
Copy-Item .env.example .env
docker compose up --build --detach
```

The stack exposes only development endpoints:

| Component | Address |
| --- | --- |
| Application | `http://localhost:5150` |
| Swagger | `http://localhost:5150/swagger` |
| RabbitMQ AMQP | `127.0.0.1:5672` |
| RabbitMQ management | `http://localhost:15672` |
| Audit-worker health | `http://localhost:5152/health` |
| Webhook-receiver health | `http://localhost:5153/health` |
| Received webhook metadata | `http://localhost:5153/deliveries` |

Use the RabbitMQ credentials selected in the local `.env` file for its management page. The webhook
receiver retains only delivery ID, event type, receipt time, and body digest; it does not expose the
signed request body.

Check readiness and follow logs:

```powershell
Invoke-RestMethod http://localhost:5150/health
Invoke-RestMethod http://localhost:5152/health
Invoke-RestMethod http://localhost:5153/health
docker compose logs --follow app audit-worker rabbitmq webhook-receiver
```

The API process probe confirms that the process is serving requests. The worker probe returns `200`
only when both PostgreSQL and the selected broker are ready; otherwise it returns `503` with a
degraded state.

### RabbitMQ delivery paths

The Compose stack declares durable request, retry, result, and dead-letter queues. Worker
infrastructure failures pass through 5-second, 30-second, and 5-minute retry queues. Invalid request
contracts and exhausted requests arrive in `caseledger.audit.verify.dead.v1`. Rejected result
contracts arrive in `caseledger.api.audit-verification-results.dead.v1`.

The API and worker both use leased outbox rows. A published message can be repeated after a process
stops before marking its row, so duplicate delivery is expected. Do not delete duplicate messages to
hide a problem: verify that the worker inbox remains one row per `jobId`, that the API job has one
deterministic `resultId`, and that only one webhook delivery row exists.

### Signed webhook delivery

Compose enables the fixed local receiver and explicitly permits insecure HTTP only inside the local
network. Hosted configuration should use a fixed HTTPS URL, a secret from the platform secret store,
and `Webhook__AllowInsecureHttp=false`.

Receivers must validate `X-CaseLedger-Timestamp` and `X-CaseLedger-Signature` against the exact raw
UTF-8 request body, then deduplicate using `X-CaseLedger-Delivery` or `Idempotency-Key`. A 2xx response
marks the row delivered. Network failures, timeouts, 408, 429, and 5xx responses retry; other 4xx
responses and exhausted attempts move the database delivery row to its terminal dead-letter state.

## Azure Service Bus provider

Azure Service Bus support is implemented but not deployed by this repository. Before enabling it,
an operator must provision one topic and two subscriptions:

| Consumer | Required subscription rule |
| --- | --- |
| TypeScript worker | Correlation `Subject = caseledger.audit.verification.requested.v1` or SQL `sys.Label = 'caseledger.audit.verification.requested.v1'` |
| ASP.NET API | Correlation `Subject = caseledger.audit.verification.result.v1` or SQL `sys.Label = 'caseledger.audit.verification.result.v1'` |

Remove each subscription's unfiltered default rule. Requests, scheduled retries, and results share
the same topic. Both consumers use PeekLock and manual settlement: successful work is completed,
invalid contracts are dead-lettered, and infrastructure failures are abandoned for redelivery.

Configure the API through deployment secrets/settings:

- `Messaging__Enabled`
- `Messaging__Provider`
- `Messaging__AzureServiceBus__ConnectionString`
- `Messaging__AzureServiceBus__TopicName`
- `Messaging__AzureServiceBus__ResultSubscriptionName`

Configure the worker through deployment secrets/settings:

- `BROKER_PROVIDER`
- `AZURE_SERVICE_BUS_CONNECTION_STRING`
- `AZURE_SERVICE_BUS_TOPIC`
- `AZURE_SERVICE_BUS_REQUEST_SUBSCRIPTION`
- `AUDIT_WORKER_DATABASE_URL`

Set the API provider to `AzureServiceBus` and the worker provider to `azure-service-bus`. Enable the
API only after the topic and filtered subscriptions exist.

The API topic and worker topic must be the same resource. Do not place connection values in tracked
files or logs. Provider startup validation reports only the invalid setting category, not its value.

Hosted webhook settings are `Webhook__Enabled`, `Webhook__DestinationUrl`,
`Webhook__SigningSecret`, and `Webhook__AllowInsecureHttp`. Keep the feature disabled until the
receiver can validate signatures and delivery IDs.

## Docker-backed E2E workflows

Install Chromium once, then run:

```powershell
npx playwright install chromium
npm run test:e2e
```

On Linux, `npx playwright install --with-deps chromium` also installs browser system packages.
Global setup recreates the isolated `caseledger-e2e` project and waits for the application at
`http://127.0.0.1:5151`. The E2E RabbitMQ listener is bound to `127.0.0.1:5673`, and the signed
webhook receiver is at `http://127.0.0.1:5154`. Global teardown removes containers and disposable
volumes.

The suite covers login, case creation, evidence hashing, distributed audit verification, a direct
PostgreSQL audit-row change, duplicate API-request and worker-result publication, idempotent result
and webhook handling, and dead-lettering a malformed request contract.

## Local observability stack

Start the distributed application and provisioned Grafana dashboard with the observability overlay:

```powershell
docker compose -f compose.yaml -f compose.observability.yaml up --build --detach
```

Additional endpoints:

- Grafana: `http://localhost:3000`
- OTLP/gRPC receiver: `127.0.0.1:4317`
- OTLP/HTTP receiver: `127.0.0.1:4318`

Use the bundled Grafana credentials only on a developer machine. The overlay binds Grafana and OTLP
receivers to loopback. The API always writes UTC JSON console logs; OTLP traces, metrics, and logs are
enabled only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

Telemetry includes operational identifiers, bounded categories, counts, sizes, durations, and
runtime signals. It excludes evidence filenames, user email addresses, passwords, cookies, broker
or webhook secrets, message bodies, and uploaded content.

## Render demo behavior

The current public demo is an image-backed Render service. CI publishes immutable API and worker
images to GHCR, and the API image is selected for Render only after the repository checks pass.
The service has no hosted worker or broker; messaging and webhooks therefore stay disabled and the
client uses synchronous verification fallback.

The checked-in `render.yaml` remains a valid repo-based Blueprint option and explicitly keeps both
features disabled. It does not describe the separate image-backed service's update mechanism, and
it does not claim an Azure Service Bus deployment.

## Shutdown

Stop the normal stack while retaining database and broker volumes:

```powershell
docker compose down
```

Adding `--volumes` deletes local PostgreSQL, RabbitMQ, and observability data. The Compose services,
local webhook receiver, and LGTM backend are development tools; production requires managed secret
storage, TLS, access control, backups, retention, monitoring, and deliberate dead-letter recovery.
