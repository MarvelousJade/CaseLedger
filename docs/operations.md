# Operations

This guide covers development, the distributed Compose demonstration, E2E verification, and the
operator-controlled Azure deployment package. It is not a complete production operations runbook.

## Fast verification

From the repository root:

```powershell
npm ci
npm ci --prefix apps/web
npm ci --prefix apps/audit-worker
npm ci --prefix apps/graphql-gateway
npm run check
```

Useful focused commands:

```powershell
npm test --prefix apps/web
npm run check --prefix apps/graphql-gateway
dotnet test CaseLedger.slnx
npm test --prefix apps/audit-worker
npm test --prefix tools/audit-verifier
npm test --prefix tools/webhook-receiver
```

`npm run check` runs frontend lint/build/tests, gateway generation/build/integration tests, the .NET
build and API suite, worker type checking and tests, the independent verifier tests, and
webhook-receiver tests.

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
- GraphQL gateway: `http://localhost:5155/graphql`
- GraphQL gateway health: `http://localhost:5155/health`

This mode uses SQLite. Messaging and webhooks are disabled by default, so the queue endpoint returns
`503` and the React client falls back to synchronous audit verification. Swagger uses the same
cookie session as the client. Its request interceptor obtains an antiforgery token automatically,
so use the login operation before calling protected routes.

The React client exchanges its authenticated cookie session for a short-lived gateway JWT. For
standalone gateway runs, `CASELEDGER_API_URL`, `JWT_SIGNING_KEY`, `JWT_ISSUER`, `JWT_AUDIENCE`,
`CORS_ORIGINS`, `HOST`, and `PORT` configure the upstream and listener. The signing key, issuer, and
audience must match `Authentication__Jwt` in the API. Development defaults are local-only;
production startup requires a signing key of at least 32 characters.

SQLite files are disposable development storage and are not migrated in place. At startup,
CaseLedger compares the existing tables and columns with the current model and stops before seeding
if the file is stale. Back up any records you need, stop the API, rename or delete
`caseledger.db` in the API process working directory (or the file selected by
`ConnectionStrings__CaseLedger`), and restart to create the current schema. Use the PostgreSQL path
for environments that require data-preserving schema upgrades.

Evidence uploads are hashed by the API and stored under `apps/api/App_Data/evidence` by default.
The directory is ignored by Git. Removing it deletes local evidence objects but not database rows.

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
| GraphQL gateway | `http://localhost:5155/graphql` |
| GraphQL gateway health | `http://localhost:5155/health` |
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
Invoke-RestMethod http://localhost:5155/health
Invoke-RestMethod http://localhost:5152/health
Invoke-RestMethod http://localhost:5153/health
docker compose logs --follow app graphql-gateway audit-worker rabbitmq webhook-receiver
```

The API process probe confirms that the process is serving requests. The worker probe returns `200`
only when both PostgreSQL and the selected broker are ready; otherwise it returns `503` with a
degraded state.

Compose mounts a named volume at `/data`; the API stores evidence beneath `/data/evidence`. The
volume survives `docker compose down` but is removed by `docker compose down --volumes`.

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

### Failure inspection and guarded replay

An authenticated `Admin` can open **Operations** or call
`GET /api/admin/operations/failures` to inspect redacted API request-outbox, terminal verification,
and webhook failures. Responses contain operational IDs, case references, timestamps, attempt
counts, and sanitized error codes; they never contain payload JSON, canonical data, webhook bodies,
destinations, lock IDs, or secrets.

Eligible request and webhook rows can be replayed from the UI or their administrator-only replay
endpoints. Each command must echo the displayed `deadLetteredAt` value and include a 3–240 character
reason. The server rejects stale views, active leases, disabled runtimes, already terminal work, and
conflicting delivered/published state. An accepted replay retains the original idempotency ID and
payload, resets the retry schedule, and writes an `OperationalReplay` audit row with the actor and
previous attempt/error state. A lost publish or response can still mean the external side effect
already happened, so downstream message and webhook consumers must deduplicate the stable ID.

Terminal verification jobs are immutable: queue a new verification rather than resetting the old
job. The Operations API also does not settle or republish broker-native DLQs. Inspect RabbitMQ's
`caseledger.audit.verify.dead.v1` and `caseledger.api.audit-verification-results.dead.v1`, or the
corresponding Azure Service Bus subscription DLQs, with provider tooling. Validate the failure and
message identity before settling it; malformed or conflicting messages must not be blindly replayed.

## Azure deployment package

The `infra/azure` package defines a production-oriented foundation, but it has not been provisioned.
Review expected Azure charges and sign in to the intended subscription before running a deployment.
The package creates private-networked PostgreSQL, a Container Apps environment with API and worker
apps, Service Bus, private Blob containers, Key Vault, and separate user-assigned managed identities.
It also persists ASP.NET Core Data Protection keys in Blob and protects them with a Key Vault key.

Validate the Bicep locally without creating resources:

```powershell
./infra/azure/validate.ps1
./infra/azure/validate.ps1 -ResourceGroup caseledger-staging -EnvironmentName staging
```

For the first rehearsal, use resource group `caseledger-staging`, region `canadacentral`, logical
environment `staging`, and protected GitHub environment `azure-staging`. Run the bootstrap only
after signing in to the intended Azure subscription:

```powershell
./infra/azure/bootstrap-github-oidc.ps1 -GitHubEnvironment azure-staging
```

The environment selection defaults the identity to `caseledger-github-staging-deploy` and scopes it
to `caseledger-staging`. Store the printed Azure identifiers and required passwords only in the
matching protected environment, and set its `AZURE_RESOURCE_GROUP` variable to the bootstrap group.
Production continues to use `azure-production`, `caseledger-prod`,
and `caseledger-github-deploy`; development uses the corresponding `azure-dev` values. The manual
`Deploy Azure environment` workflow selects the protected environment from the logical environment,
accepts a `plan` operation for `what-if` or an explicit `deploy` operation, checks that both GHCR
images are anonymously pullable, and smoke-checks `/health` after deployment. See
[`infra/azure/README.md`](../infra/azure/README.md) for prerequisites and commands.

Staging and development deploy PostgreSQL Burstable `Standard_B1ms`, 32 GiB storage, seven-day
backup retention, disabled HA, one API replica, and one worker replica. Production retains the
General Purpose database and replica defaults. Both processes remain at one during the rehearsal so
their database-backed dispatchers and consumers can make progress; reduce them only after the test,
with the checked-in worker Service Bus scaler still configured.

### Service Bus topology and identity

The template creates one topic and two subscriptions:

| Consumer | Required subscription rule |
| --- | --- |
| TypeScript worker | Correlation `Subject = caseledger.audit.verification.requested.v1` or SQL `sys.Label = 'caseledger.audit.verification.requested.v1'` |
| ASP.NET API | Correlation `Subject = caseledger.audit.verification.result.v1` or SQL `sys.Label = 'caseledger.audit.verification.result.v1'` |

Each subscription has only its exact subject filter. Requests, scheduled retries, and results share
the same topic. Both consumers use PeekLock and manual settlement: successful work is completed,
invalid contracts are dead-lettered, and infrastructure failures are abandoned for redelivery.

The Bicep deployment configures the API with the Service Bus fully qualified namespace and its
user-assigned managed identity. For a manually provisioned environment, configure:

- `Messaging__Enabled`
- `Messaging__Provider`
- `Messaging__AzureServiceBus__ConnectionString`
- `Messaging__AzureServiceBus__FullyQualifiedNamespace`
- `Messaging__AzureServiceBus__ManagedIdentityClientId`
- `Messaging__AzureServiceBus__TopicName`
- `Messaging__AzureServiceBus__ResultSubscriptionName`
- `Messaging__MaximumMessageBytes`

Configure the worker through deployment secrets/settings:

- `BROKER_PROVIDER`
- `AZURE_SERVICE_BUS_CONNECTION_STRING`
- `AZURE_SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE`
- `AZURE_MANAGED_IDENTITY_CLIENT_ID`
- `AZURE_SERVICE_BUS_TOPIC`
- `AZURE_SERVICE_BUS_REQUEST_SUBSCRIPTION`
- `AUDIT_WORKER_DATABASE_URL`

Use exactly one credential mode per component: a connection string for local/manual compatibility,
or a fully qualified namespace with Azure credentials. The Bicep path uses managed identities and
disables Service Bus local/SAS authentication. Set the API provider to `AzureServiceBus` and the
worker provider to `azure-service-bus`; enable them only after the topic and filtered subscriptions
exist. The Standard template caps serialized verification snapshots at 192 KiB so application
payloads retain headroom under the broker limit.

The API topic and worker topic must be the same resource. Do not place connection values in tracked
files or logs. Provider startup validation reports only the invalid setting category, not its value.

Hosted webhook settings are `Webhook__Enabled`, `Webhook__DestinationUrl`,
`Webhook__SigningSecret`, and `Webhook__AllowInsecureHttp`. Keep the feature disabled until the
receiver can validate signatures and delivery IDs.

### Evidence, session keys, and sign-in

The Azure API uses its managed identity to write evidence objects to the private `evidence` Blob
container. It also writes its shared Data Protection key ring to the separate `data-protection`
container and wraps those keys with a versionless Key Vault key. Do not substitute account keys,
SAS values, or version-pinned Key Vault identifiers in tracked settings.

The template has no implicit authentication mode: every deployment must explicitly choose `Entra`,
`Demo`, or `DemoAndEntra`. For Entra, configure one tenant ID, application client ID, protected
client secret, registered callback URI, and preferably the exact Entra object ID that will receive
the one-time seeded-administrator mapping. The mapping is immutable and email claims are never used
for linking. Tenant-wide `AutoProvisionAnalyst` is available only as a deliberate alternative and
should normally remain disabled. Demo modes require both seeded passwords; credential hints remain
off unless the environment is intentionally public. Never reuse the public Render credentials.

Do not confuse the two client IDs. `AZURE_CLIENT_ID` is the user-assigned deployment identity trusted
by the GitHub environment's OIDC subject. `CASELEDGER_ENTRA_CLIENT_ID` is the separate single-tenant
application registration used for browser sign-in. Create that app registration before deployment,
then register the emitted HTTPS `/signin-oidc` callback before the Entra smoke test. Prefer Entra-only
authentication with `CASELEDGER_ENTRA_AUTO_PROVISION_ANALYST=false` and an exact bootstrap
administrator object ID for staging.

### Staging rehearsal and cost shutdown

Inspect `what-if` before deploying and reject unexpected Premium Service Bus, PostgreSQL HA, or
General Purpose database resources. Free-account PostgreSQL allowances are offer-dependent; Service
Bus Standard, Log Analytics, storage, Key Vault, and Container Apps can still consume free credit.
Set a resource-group budget and alerts before deployment.

After deployment, verify Entra login and `/api/auth/me`, create a case, upload evidence, and queue an
asynchronous audit verification through completion. That path exercises PostgreSQL, Blob storage,
both managed identities, both filtered Service Bus subscriptions, and the worker result outbox.
Preserve a session cookie across an API revision restart to verify the Blob/Key Vault Data Protection
key ring, inspect both dead-letter subscriptions, and rehearse redeploying the previous image digest.

When testing is complete, reduce both Container Apps minimum replica counts to zero, then stop or
delete PostgreSQL. Stopping removes compute charges but retains storage charges, and Flexible Server
automatically starts again after seven days. Delete the staging resource group when it is no longer
needed. Because the vault has 90-day purge protection, a deleted vault name cannot immediately be
reused; recover it for an iterative rehearsal or deploy with a different naming prefix.

Scale the API back to one before resuming a durability rehearsal: its database outbox and Service
Bus result consumer are background services, while its current Azure scaler observes HTTP traffic
only. If the staging group was deleted, rerun the OIDC bootstrap; the group-scoped deployment
identity cannot recreate the resource group that contained it.

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

If Windows or another local service reserves those host ports, set
`CASELEDGER_E2E_APP_PORT`, `CASELEDGER_E2E_RABBITMQ_PORT`,
`CASELEDGER_E2E_GATEWAY_PORT`, `CASELEDGER_E2E_RABBITMQ_MANAGEMENT_PORT`, and
`CASELEDGER_E2E_WEBHOOK_PORT` before running the
suite. Compose, health checks, browser requests, and broker/webhook helpers use the overrides
consistently.

The suite covers login, case creation, server-side evidence hashing and persistence, distributed audit verification, a direct
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

The Render service currently hosts only the existing API/SPA image. It does not deploy the Node.js
gateway, so the gateway page is a local/Compose capability until a separate hosted service and URL
are configured.

Render currently uses the local evidence provider without a persistent disk. Uploaded bytes can be
lost when the free service is replaced or recycled even while Neon retains the evidence metadata;
use the Azure Blob provider or another durable object store for a real hosted evidence workflow.

The checked-in `render.yaml` remains a valid repo-based Blueprint option and explicitly keeps both
features disabled. It does not describe the separate image-backed service's update mechanism, and
it does not claim an Azure Service Bus deployment.

## Shutdown

Stop the normal stack while retaining database and broker volumes:

```powershell
docker compose down
```

Adding `--volumes` deletes local PostgreSQL, RabbitMQ, evidence objects, and observability data. The Compose services,
local webhook receiver, and LGTM backend are development tools; production requires managed secret
storage, TLS, access control, backups, retention, monitoring, and deliberate dead-letter recovery.
