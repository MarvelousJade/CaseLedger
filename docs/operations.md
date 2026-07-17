# Local operations

This guide covers development and demonstration workflows. The local credentials, telemetry
backend, and storage defaults below are not a production operations design.

## API documentation

With the API running on port 5150:

- Swagger UI: `http://localhost:5150/swagger`
- OpenAPI JSON: `http://localhost:5150/swagger/v1/swagger.json`
- Health probe: `http://localhost:5150/health`

Swagger uses the same cookie-authenticated API as the React client. Call the login endpoint first
to establish a session before invoking protected operations.

## Fast verification

From the repository root:

```powershell
npm ci
npm ci --prefix apps/web
npm run check
```

The root check runs the frontend lint/build, Vitest component tests, .NET build/API tests, and the
independent Node.js verifier tests. Useful focused commands are:

```powershell
npm test --prefix apps/web
dotnet test CaseLedger.slnx
npm test --prefix tools/audit-verifier
```

## PostgreSQL browser workflows

Playwright uses `compose.e2e.yaml` to build the production image and start a disposable PostgreSQL
17 database on an isolated Compose project. Install Chromium once, then run:

```powershell
npx playwright install chromium
npm run test:e2e
```

On Linux, `npx playwright install --with-deps chromium` also installs browser system packages.
Global setup removes any previous `caseledger-e2e` stack and volume, waits for the application at
`http://127.0.0.1:5151`, and global teardown removes the disposable stack after the run.

The workflows cover analyst login, case creation, browser-side evidence hashing and registration,
successful audit verification, a direct audit-row change through PostgreSQL, and detection of that
tampering in the browser. They do not reuse the normal development database.

## Local observability stack

Start the application, PostgreSQL, OpenTelemetry LGTM image, and provisioned CaseLedger dashboard
with the Compose overlay:

```powershell
docker compose -f compose.yaml -f compose.observability.yaml up --build --detach
```

Then open:

- Application: `http://localhost:5150`
- Grafana: `http://localhost:3000`
- OTLP/gRPC receiver: `127.0.0.1:4317`
- OTLP/HTTP receiver: `127.0.0.1:4318`

Grafana's local development default is username `admin` and password `admin`. Use it only on a
developer machine; do not expose that instance or reuse those credentials in a shared environment.
The Compose file binds Grafana and the OTLP receivers to loopback.

Follow the application's structured JSON console logs with:

```powershell
docker compose -f compose.yaml -f compose.observability.yaml logs --follow app
```

The API always writes UTC JSON console logs. Trace, metric, and log export over OTLP is conditional:
the exporters are enabled only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The observability overlay
sets it to the local collector; a normal local or container run does not export remotely.

CaseLedger telemetry is intentionally content-minimized. It includes operational identifiers,
bounded categories, counts, byte sizes, durations, and request/runtime signals. It does not include
evidence filenames, user email addresses, passwords, session cookies, authorization secrets,
request bodies, or uploaded file content.

Stop the stack while retaining database and telemetry volumes:

```powershell
docker compose -f compose.yaml -f compose.observability.yaml down
```

Adding `--volumes` also deletes both PostgreSQL and observability data. The bundled LGTM service is
for local development and demonstrations only; production needs managed authentication, TLS,
retention, access control, backups, and alerting.
