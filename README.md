# Japanese Learning User Service

The .NET backend submodule of the Japanese Learning application provides account registration, login, access and refresh tokens, and claim-based user identity. Angular is the frontend; Quarkus owns vocabulary, flashcards, and the API Gateway. This repository contains the user service, not those clients or services.

Implemented capabilities include active-user checks, User/Admin roles, RS256 access tokens, refresh-token rotation and logout, remote JWKS verification, SQL Server persistence, structured logs, health probes, and Prometheus metrics. Email verification, password reset, account/profile editing, role administration, and OpenID Connect discovery are not implemented.

## Technology stack

Versions below come from the project files and Dockerfile.

| Area | Implementation |
| --- | --- |
| Runtime | .NET / ASP.NET Core 9; all projects target `net9.0` |
| Language | C# SDK default for .NET 9 (C# 13); nullable references and implicit usings enabled |
| Application pipeline | MediatR 14.2.0; FluentValidation 12.1.1 |
| Persistence | SQL Server; Dapper 2.1.79; Microsoft.Data.SqlClient 7.0.2; stored procedures |
| Security | JwtBearer 9.0.8; Identity password hashing via Microsoft.Extensions.Identity.Core 9.0.17; Microsoft IdentityModel JWT APIs; .NET RSA/SHA-256 |
| API documentation | Swashbuckle.AspNetCore 9.0.6 |
| Observability | Serilog.AspNetCore 9.0.0, Serilog.Sinks.File 7.0.0, prometheus-net.AspNetCore 8.2.1 |
| Tests | xUnit 2.9.2, Moq 4.20.72, Microsoft.NET.Test.Sdk 17.12.0, coverlet.collector 6.0.2 |
| Packaging | Multi-stage Docker build with digest-pinned .NET 9 SDK and ASP.NET runtime images |

There is no `global.json`, explicit `LangVersion`, Entity Framework Core, or full ASP.NET Core Identity account store. The SQL Server and Flyway versions are not pinned in this submodule.

## Architecture

The solution uses four layers with dependencies pointing toward Application and Domain:

```mermaid
flowchart LR
    API["API"] --> Application["Application"]
    API --> Infrastructure["Infrastructure"]
    Infrastructure --> Application
    Infrastructure --> Domain["Domain"]
    Application --> Domain
```

- **API:** controllers, HTTP contracts, bearer authentication, authorization, exception handling, correlation, Swagger, metrics, and health endpoints.
- **Application:** feature-oriented MediatR commands, handlers, validators, responses, and persistence/security interfaces.
- **Domain:** user and refresh-token entities and the User/Admin role enum; no project dependencies.
- **Infrastructure:** Dapper repositories, SQL connections, password hashing, token generation, RSA loading, current-user claims, and SQL health checks.

Startup calls `AddApplication` and `AddInfrastructure` to register implementations with dependency injection. Authentication commands flow through controller `ISender` → validation behavior → logging behavior → handler → repository/security abstractions. Repositories pass cancellation tokens to asynchronous Dapper operations. The `me` and `admin-test` endpoints read authenticated claims directly.

## Prerequisites

- Git and a .NET 9 SDK.
- A reachable SQL Server instance and an existing application database with the migrations applied.
- Readable unencrypted RSA private PEMs of at least 2048 bits configured in a nonempty indexed signing list.
- A trusted ASP.NET Core development HTTPS certificate for the HTTPS launch profile.
- Docker with Linux containers only if using the supplied Dockerfile.
- An externally configured Flyway runner for database migrations.

## Getting started

Use the .NET checkout supplied by your parent application repository. Run the following commands from the directory containing `JapaneseLearning.User.sln`:

```powershell
dotnet --list-sdks
dotnet restore JapaneseLearning.User.sln
```

1. Start SQL Server through your development environment and create/select the application database.
2. Apply all scripts in `db/migration` through the environment's Flyway runner before using authentication endpoints. See [Database and persistence](#database-and-persistence).
3. Supply database configuration and readable RSA key paths as described below.
4. Run the API:

```powershell
dotnet run --project src/JapaneseLearning.User.Api --launch-profile https
```

The `https` profile sets Development and binds `https://localhost:7066` and `http://localhost:5116`. The `http` profile binds only `http://localhost:5116`. HTTPS redirection is enabled by default; for intentional HTTP-only local development, set `$env:Http__RedirectToHttps = 'false'` before using that profile.

With the HTTPS profile:

- Swagger UI: https://localhost:7066/swagger
- OpenAPI JSON: https://localhost:7066/swagger/v1/swagger.json
- Public signing keys (independent service): http://localhost:8083/.well-known/jwks.json
- Readiness: https://localhost:7066/health/ready

A successful liveness response does not establish database readiness or migration completeness.

## Environment configuration

The API uses ASP.NET Core configuration: `appsettings.json`, environment-specific JSON, environment variables, and command-line overrides. Environment variables use double underscores for nested keys, for example `Jwt__Issuer`. No `UserSecretsId` is configured in the API project.

`src/JapaneseLearning.User.Api/appsettings.json` supplies shared defaults. `appsettings.Development.json` supplies local key paths and application Debug logging. There is no Production-specific settings file; production must supply database credentials and RSA settings externally. Keep credentials and private keys outside Git and image build arguments.

### Database settings

Choose **one** configuration mode:

| Setting | Meaning |
| --- | --- |
| `Database:ConnectionString` | Complete SQL Server connection string; empty by default |
| `Database:Server`, `Database:Name`, `Database:User`, `Database:Password` | Alternative split configuration; all four are required |
| `Database:TrustServerCertificate` | Split-mode certificate trust override; defaults to false |

A complete connection string must identify the server and database and provide appropriate authentication. Windows integrated authentication is supported through the complete connection-string mode. Mixing a nonempty connection string with split credentials fails startup. The application forces `PersistSecurityInfo=false`.

For a local Windows SQL Server configured to accept your Windows identity, this credential-free example can be adapted:

```powershell
$env:Database__ConnectionString = 'Server=localhost;Database=JapaneseLearningUser;Integrated Security=True;TrustServerCertificate=True'
```

`JapaneseLearningUser` is an example database name, not a hard-coded requirement. The certificate trust override above is for local development; use properly validated SQL Server certificates in deployment. Supply SQL credentials through your environment's secret mechanism without printing the resulting connection string.

### JWT settings

Signing requires an indexed private-key list supplied through the parent Compose `env_file`. ASP.NET does not read dotenv
files itself: Compose injects their entries into the container environment.

```dotenv
APP_SECURITY_SIGNING_KEYS_0_KID=user-rsa-v1
APP_SECURITY_SIGNING_KEYS_0_PRIVATE_KEY_PATH=/run/keys/jwt/userservice-private-v1.pem
APP_SECURITY_SIGNING_KEYS_0_ACTIVE=false
APP_SECURITY_SIGNING_KEYS_1_KID=user-rsa-v2
APP_SECURITY_SIGNING_KEYS_1_PRIVATE_KEY_PATH=/run/keys/jwt/userservice-private-v2.pem
APP_SECURITY_SIGNING_KEYS_1_ACTIVE=true
```

Use contiguous indices from zero, unique IDs (1-128 ASCII letters, digits, hyphens or underscores), readable unencrypted RSA private PEMs of at least 2048 bits, and
exactly one active entry. Every configured private key is checked at startup,
including inactive entries; only the active key is retained for signing. Its
`kid` is used in the JWT header. Publish its corresponding public key and ID in
independent JWKS before activating it. Signing-key selection does not change
JWT claims, RS256, lifetimes, refresh/logout, or remote verification.

The signing list is mandatory. Missing entries, invalid keys or anything other
than one active key fail startup. Public parameters are derived from the private
key; .NET does not mount or load public PEM files. JWT verification still fetches
public keys from independent JWKS. Standard `Jwt:SigningKeys` binding is also
available; do not combine it with the `APP_SECURITY_SIGNING_KEYS_*` list.
Recreate the process/container after changing signing keys or the active flag.

In the root project, copy `.env.user-api.example` to the ignored `.env.user-api`.
The example deliberately uses the existing development key and ID. Set
`USER_API_SIGNING_ENV_FILE` for an environment-specific file and
`USER_API_SIGNING_KEYS_HOST_PATH` for the host signing-key directory. Compose
mounts that directory read-only at `/run/keys/jwt`, only in .NET. Private keys
must never be placed in the separate Spring JWKS public directory.

| Setting | Default / requirement |
| --- | --- |
| `Jwt:JwksUrl` | Required independent JWKS URL (`Jwt__JwksUrl` environment variable) |
| `Jwt:RequireHttpsMetadata` | `true`; explicitly use `false` for trusted internal/local HTTP |
| `Jwt:Issuer` | `JapaneseLearning.User` |
| `Jwt:Audience` | `JapaneseLearning` |
| `Jwt:AccessTokenExpirationMinutes` | 15; allowed range 1–1440 |
| `Jwt:RefreshTokenExpirationDays` | 7; allowed range 1–365 |

Issuer and audience must be nonblank, at most 256 characters, and contain no control characters. `Jwt:Secret` is not used.

Native Development also requires an explicit signing list; no signing paths or
IDs are supplied by appsettings. For example, from the parent root checkout:

```powershell
$env:APP_SECURITY_SIGNING_KEYS_0_KID = 'japanese-learning-local-1'
$env:APP_SECURITY_SIGNING_KEYS_0_PRIVATE_KEY_PATH = (Resolve-Path secrets/private/user-api/private.pem).Path
$env:APP_SECURITY_SIGNING_KEYS_0_ACTIVE = 'true'
```

The loader imports PKCS#8/PKCS#1 unencrypted RSA private PEMs, validates every
configured key and minimum size, and loads the active signer once before HTTP
starts. No separate public-key file or local public/private pair check is used.

### Other settings

- `Http:RedirectToHttps`: defaults to true in startup code; the Docker image sets it to false.
- `AllowedHosts`: `*` in base settings.
- `Serilog` and `LogFiles`: see [Logging and observability](#logging-and-observability).
- No Angular origin, Quarkus URL, gateway URL, or CORS policy is configured here. The JWT verifier uses the configured outbound JWKS client.

## Project structure

```text
JapaneseLearning.User.sln
src/
  JapaneseLearning.User.Api/
    Authentication/       JWT bearer validation
    Controllers/          Authentication endpoints
    Common/               Errors, response types, correlation and logging
    Health/               Liveness/readiness mappings
    Observability/        Prometheus metrics
    Properties/           Local launch profiles
    Program.cs            Composition and middleware
    appsettings*.json     Shared and development settings
  JapaneseLearning.User.Application/
    Abstractions/         Persistence and security interfaces
    Auth/                 Register, Login, Refresh, Logout use cases
    Common/               Pipeline behaviors and application exceptions
  JapaneseLearning.User.Domain/
    Entities/             User, refresh token, and roles
  JapaneseLearning.User.Infrastructure/
    Configuration/        Database and JWT options
    Database/             SQL connection factory
    Persistence/          Dapper repositories
    Security/             Password hashing, tokens, RSA keys, claims
    HealthChecks/         SQL connectivity check
db/migration/             Versioned SQL migrations V1–V4
tests/JapaneseLearning.User.UnitTests/
    Security/             JWT, JWKS, key startup, database options
    Logging/              Correlation, safe logs, authentication events
    Health/               Probe behavior
    Observability/        Metrics behavior
Dockerfile
.dockerignore
```

See [AGENTS.md](AGENTS.md) for repository contribution and architectural conventions.

## Authentication and authorization

### Account and token flow

1. **Register:** accepts username, email, and password. Username is required and limited to 100 characters; email must be valid and at most 255 characters; password length is 8–100. The handler trims usernames, trims/lowercases email, checks uniqueness, hashes the password, and creates an active `User` account. Registration returns account metadata, not tokens. There is no activation/email-verification step.
2. **Login:** normalizes email, retrieves the user, rejects inactive accounts, and verifies the password. The service uses ASP.NET Core Identity's `PasswordHasher<object>` behind its own abstraction. A verification result requesting rehash is accepted, but the hash is not updated automatically. Successful login returns `accessToken`, `refreshToken`, and `expiresIn` in seconds.
3. **Refresh:** accepts a refresh token in the JSON body, checks its hash, revocation, expiration, and active user, then revokes it and persists a newly generated token. The response contains a new access/refresh pair with a fresh configured lifetime.
4. **Logout:** accepts a refresh token in the JSON body and revokes that token. Missing, expired, or already revoked tokens return 401. It does not revoke other sessions or invalidate already issued access tokens.

Refresh tokens contain 64 cryptographically random bytes encoded as Base64. Only their SHA-256 hex hashes are stored in SQL Server, with user ID, creation, expiration, and revocation timestamps. Raw tokens are returned to the caller.

**Rotation limitation:** checking, revoking, and inserting tokens are separate database operations without a shared transaction or atomic consume operation. Concurrent refresh requests are not guaranteed to produce only one successor; an insertion failure after revocation can require a new login. There is no token-family reuse detection or automatic expired-token cleanup.

### Access tokens and roles

Access tokens use **RS256**, a configured `kid` header, and claims `sub` (user GUID), `unique_name`, `email`, `role`, `jti`, `iss`, `aud`, and `exp`. Each issued token has a new `jti`.

Bearer validation retrieves public keys from the independent JWKS service configured by `Jwt:JwksUrl`; it never falls back to local signing keys or the legacy endpoint. It requires a signed, unexpired token with the configured issuer, audience, matching key ID, and RS256 algorithm. Clock skew is zero. Standard inbound claim mapping supplies `ICurrentUser` with user ID, username, email, and role.

Development settings use `http://localhost:8083/.well-known/jwks.json`. Docker supplies `http://jwks-api:8080/.well-known/jwks.json`. IdentityModel caches configuration, refreshes automatically after 15 minutes on demand, and throttles refresh requests to one minute. HTTP retrieval has a five-second timeout. Key retrieval is lazy: startup and login do not require JWKS. A cold verifier or unknown key fails authentication during an outage; cached keys continue to validate signatures and claims. Last-known-good fallback to superseded configurations is disabled. For rotation, publish overlapping public keys with distinct IDs before switching the issuer, and retain the old public key until issued tokens expire plus cache/rollout time. Private signing keys are validated at startup; public verification keys come exclusively from JWKS.

- `GET /api/auth/me` requires authentication and returns claims, without reloading the account.
- `GET /api/auth/admin-test` requires the `Admin` role.
- Registration always assigns `User`; there is no role-management endpoint or admin seeding migration.
- Register, login, refresh, and logout do not require an access token. Refresh/logout rely on possession of the refresh token.
- Account status and role changes are not rechecked on ordinary bearer requests; existing claims remain effective until token expiry.

### Angular and Quarkus integration boundary

Clients can log in through the JSON API and send `Authorization: Bearer <access-token>` on protected requests. Refresh/logout bodies use `{ "refreshToken": "<refresh-token>" }`. This service does not issue authentication cookies or implement frontend token storage.

Public RSA signing keys are published exclusively by the independent Spring JWKS service. The former .NET `GET /.well-known/jwks.json` route has been removed and returns 404.

Configure public-key consumers to use the independent JWKS URL and enforce signature, issuer, audience, expiration, and their own authorization rules. Consumers never need the private signing key. .NET does not require a local public PEM.

## Database and persistence

SQL Server stores:

- **Users:** identity, unique username/email, password hash, role, active status, and timestamps.
- **RefreshTokens:** hashed tokens linked to users, expiry, and optional revocation time.

Dapper repositories call parameterized `dbo.usp_Users_*` and `dbo.usp_RefreshTokens_*` stored procedures. Each operation opens its own connection. There is no EF Core migration tooling or Unit of Work implementation.

Apply the Flyway-style scripts in order:

| Migration | Purpose |
| --- | --- |
| `V1__create_users_table.sql` | Users and uniqueness indexes |
| `V2__create_user_stored_procedures.sql` | User creation and existence checks |
| `V3__create_refresh_tokens.sql` | Refresh tokens, token procedures, user lookup procedures |
| `V4__add_user_role.sql` | Roles and updated user procedures |

The database must already exist. Point the environment's Flyway SQL location at this checkout's `db/migration` and target the same database as the API. Apply through V4 before running account operations. The API and Docker image do not execute migrations.

**Migration setup gap:** this submodule has no Flyway executable/version, connection configuration, Compose service, or migration wrapper. Obtain the exact invocation and infrastructure startup commands from the parent environment; no repository-specific migration command can be verified here. Do not use `dotnet ef database update`. Add new versioned migrations rather than editing applied ones.

## API documentation

Swagger UI is at `/swagger`, with JSON at `/swagger/v1/swagger.json`. In the current source, Swagger is enabled in **all environments** because its Development guard is commented out.

| Endpoint | Access | Success |
| --- | --- | --- |
| `POST /api/auth/register` | Anonymous | 201, account metadata |
| `POST /api/auth/login` | Anonymous | 200, token pair and expiry seconds |
| `POST /api/auth/refresh` | Refresh token in body | 200, replacement token pair |
| `POST /api/auth/logout` | Refresh token in body | 204, no body |
| `GET /api/auth/me` | Bearer token | 200, current identity claims |
| `GET /api/auth/admin-test` | Bearer token with Admin role | 200, role-check response |

Use Swagger to register with `username`, `email`, and `password`, then log in with `email` and `password`. Enter the access token in Swagger's **Authorize** control before calling `me`. Swagger declares a global Bearer security requirement, although the server permits the anonymous operations listed above.

## Error handling and responses

Registration, login, refresh, `me`, and `admin-test` return `{ "success": true, "data": ..., "meta": ... }`. Their existing camelCase DTO fields now appear inside `data`; HTTP status codes are unchanged (201 for registration, 200 for the others). `meta` contains the same UTC timestamp, distributed trace ID and correlation ID as error responses. Logout remains 204 with no body and exposes IDs through response headers. Health checks and metrics retain their own formats.

Centralized exception handling maps validation failures to 400, authentication failures to 401, forbidden access to 403, missing resources to 404, conflicts to 409, and unexpected exceptions to a generic 500. Empty error statuses are also given a common JSON response.

Error envelopes match the Quarkus structure: `success: false`, `error` with `code`, `message`, and a `details` array, plus `meta` containing UTC `timestamp`, `traceId`, and `correlationId`. Absent field details are `[]`; `data` and the former top-level `traceId` are omitted consistently in both MVC and middleware responses. FluentValidation errors contain field details, while malformed/model-binding failures use a generic validation message. Unexpected errors do not expose exception messages or stack traces.

`meta.correlationId` matches `X-Correlation-ID` and the log's `CorrelationId`; `meta.traceId` matches the distributed Activity trace ID in logs and the response header `X-Distributed-Trace-Id`. An incoming W3C `traceparent` continues that trace. Missing Activity context gets a request-scoped generated trace ID. Existing clients using the former top-level `traceId` must read `meta.correlationId` instead. Success callers must read DTOs from `data`; the updated Angular auth client unwraps the envelope and also accepts legacy plain DTOs during rolling deployment. Deploy the frontend before the backend, or update them together.

Example successful `/api/auth/me` response:

```json
{
  "success": true,
  "data": {
    "userId": "6255fdb2-ea35-47c8-b7e0-8c2cbc11e103",
    "username": "sample-user",
    "email": "user@example.com",
    "role": "User"
  },
  "meta": {
    "timestamp": "2026-11-04T08:00:00Z",
    "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
    "correlationId": "request-123"
  }
}
```

Example error response:

```json
{
  "success": false,
  "error": {
    "code": "UNAUTHORIZED",
    "message": "Authentication is required.",
    "details": []
  },
  "meta": {
    "timestamp": "2026-11-04T08:00:00Z",
    "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
    "correlationId": "request-123"
  }
}
```

## Logging and observability

Serilog writes newline-delimited JSON to console and rolling files, while application code uses `ILogger<T>`. A bootstrap logger captures startup failures. Events include service/environment, correlation, Activity trace/span IDs when present, structured request completion, MediatR timing, and authentication outcomes.

The middleware accepts a single `X-Correlation-ID` containing 1–64 ASCII letters, digits, underscores, or hyphens; otherwise it creates one. Request logs omit query strings, arbitrary headers, and bodies. `RequestPath` and `RequestRoute` contain the matched route template (`/unmatched` when no route matches), avoiding arbitrary path values. Safe exception diagnostics retain selected types/frames and SQL error metadata while excluding arbitrary messages and data. Do not add credentials, tokens, hashes, private keys, or authentication payloads to logs; the formatter is not a general secret scanner.

Request scopes attach HTTP method, route, controller/action and handler method/type, server request/connection IDs, network protocol, scheme, direct peer address/port, correlation ID, Activity trace/span/parent-span IDs. Hosting extracts W3C `traceparent`; callers should propagate it alongside `X-Correlation-ID`. An optional `X-Caller-Service` must be a single value with the same character/length rules as the correlation ID. It is recorded as `ClaimedCallerService` (otherwise `unknown`), not authenticated service identity. Forwarded IP headers are not trusted by this logging code; behind a gateway, `PeerAddress` identifies the direct peer.

Completion events include numeric status/duration, content lengths when known, authenticated user ID when available, `Outcome`, `ErrorCode`, `ExceptionType`, `IsSlow`, and response-started state. All 4xx/5xx and slow requests produce Warning summaries; the central handler owns the single Error diagnostic for unexpected failures. Client disconnects are logged with synthetic status 499 and `CLIENT_CLOSED_REQUEST`; logging does not change the response status. MediatR operation events carry `OperationName`/`RequestType`, timing and success/failure/cancellation, without serializing commands. Controller methods identify API entry points; safe exception frames identify deeper failing methods. Routine method-by-method payload logging is intentionally avoided.

For collectors, the JSON envelope retains `Timestamp`, `Level`, `MessageTemplate`, `RenderedMessage`, and nested `Properties`. Named event IDs also produce `Properties.EventName`. Common metadata includes `ServiceName`, `Environment`, assembly `ServiceVersion`, `ServiceInstanceId` (host/container name), and `LogSchemaVersion=1`. Existing IDs remain; application operation IDs are 1100 started, 1101 completed, 1102 failed, and 1103 cancelled. Validation can reject a command before the operation behavior runs; the HTTP summary still records the failure.

Example request event (abbreviated; values are illustrative):

```json
{"Timestamp":"2026-11-04T08:00:00Z","Level":"Warning","MessageTemplate":"HTTP request completed: {RequestMethod} {RequestPath} with status {StatusCode} in {ElapsedMilliseconds}ms for {UserId}","Properties":{"EventName":"HttpRequestCompleted","ServiceName":"JapaneseLearning.User.Api","LogSchemaVersion":1,"CorrelationId":"request-123","TraceId":"4bf92f3577b34da6a3ce929d0e0e4736","ClaimedCallerService":"vocabulary-api","RequestMethod":"POST","RequestRoute":"api/auth/login","Controller":"Auth","Action":"Login","HandlerMethod":"Login","StatusCode":401,"ElapsedMilliseconds":24.5,"Outcome":"failure","ErrorCode":"INVALID_CREDENTIALS","IsSlow":false}}
```

Fluent Bit can parse each line as JSON and extract nested properties. Use low-cardinality Loki labels such as service and environment; keep correlation/trace/user IDs as searchable fields. Collect either console output or rolling files to avoid duplicate ingestion. No Fluent Bit/Loki service or tracing exporter is installed by these logging changes.

| Configuration key | Default |
| --- | --- |
| `LogFiles:Directory` | `logs`, relative to process working directory |
| `LogFiles:RetentionDays` | 30 |
| `LogFiles:FileSizeLimitBytes` | 104857600 (100 MiB) |
| `LogFiles:RetainedFileCountLimit` | 300 |
| `Serilog:MinimumLevel:Default` | `Information` |
| `Logging:SlowRequestThresholdMilliseconds` | 1000; positive integer, env `Logging__SlowRequestThresholdMilliseconds` |
| `Serilog:MinimumLevel:Override:JapaneseLearning.User` | `Information`; `Debug` in Development |

Files use `user-api-YYYYMMDD.json`, with suffixes for size rolls. Age and count limits both apply; high volume can shorten retained history. Retention is applied on file opening/rolling, not by a background cleanup service. Restart after changing file settings. Microsoft/System levels default to Warning, with hosting lifecycle events at Information.

Anonymous operational endpoints:

- `/health/live`: HTTP liveness without database access.
- `/health/ready` and `/health`: SQL connection readiness with a three-second check timeout; 200 `Healthy` or 503 `Unhealthy`. They do not verify tables, stored procedures, or migration versions.
- `/metrics`: Prometheus exposition with .NET runtime statistics and request count, duration, and in-progress metrics. Request labels use bounded method/route categories and status codes; health traffic is excluded.

No Prometheus server, dashboards, external log collector, or distributed-tracing exporter is configured in this submodule.

## Available commands

Run from the .NET repository root. These are source-checked commands; their presence is not a claim that a build or deployment has been executed.

```powershell
# Solution-level
dotnet restore JapaneseLearning.User.sln
dotnet build JapaneseLearning.User.sln -c Release --no-restore
dotnet test JapaneseLearning.User.sln -c Release --no-build

# API only
dotnet run --project src/JapaneseLearning.User.Api --launch-profile https
dotnet publish src/JapaneseLearning.User.Api/JapaneseLearning.User.Api.csproj -c Release -o artifacts/publish

# Test project only (builds as needed)
dotnet test tests/JapaneseLearning.User.UnitTests/JapaneseLearning.User.UnitTests.csproj

# Optional coverage collection using the referenced collector
dotnet test tests/JapaneseLearning.User.UnitTests/JapaneseLearning.User.UnitTests.csproj --collect:"XPlat Code Coverage"

# Container build
docker build -t japanese-learning-user:local .
```

The solution test command using `--no-build` assumes the preceding Release build succeeded. Migration commands depend on the external runner described above.

## Testing

`JapaneseLearning.User.UnitTests` contains xUnit tests with Moq boundary substitutes. Coverage includes JWT claims and rejection rules, RSA startup validation, remote JWKS caching/rotation and retired-route regression checks, database option validation, authentication events, correlation/error handling, log formatting/retention, health failure/recovery, and metrics.

Despite the project name, some tests start local HTTP hosts on ephemeral loopback ports. Tests require temporary-file and loopback access. RSA fixtures create disposable keys in the OS temporary directory; SQL dependencies are mocked. There is no separate integration-test project or live SQL migration/repository test suite.

The Coverlet collector is referenced and supports the optional command above. No coverage threshold, HTML reporting pipeline, or CI coverage gate is configured here.

## Build and deployment

The publish command creates a framework-dependent deployment requiring the ASP.NET Core 9 runtime. Run `dotnet JapaneseLearning.User.Api.dll` from the publish directory with database and JWT settings supplied. Launch profiles apply to local `dotnet run`, not published deployments.

The Dockerfile restores, builds, and publishes the API, then copies only published output into an ASP.NET Core 9 runtime image. It runs as the image's non-root `app` user via `APP_UID`, listens on HTTP port 8080, sets Production, disables HTTPS redirection, and includes curl. TLS termination belongs in the deployment gateway for this configuration.

Example standalone PowerShell invocation, after setting `Database__ConnectionString` securely in the host environment:

```powershell
docker run --rm --name japanese-learning-user -p 8080:8080 `
  --env Database__ConnectionString `
  --env-file .env.user-api `
  --env Jwt__JwksUrl `
  --env Jwt__RequireHttpsMetadata `
  --mount "type=bind,source=C:/secure/japanese-learning/private/user-api,target=/run/keys/jwt,readonly" `
  --mount "type=volume,source=japanese-learning-user-logs,target=/app/logs" `
  japanese-learning-user:local
```

Replace the host key directory with the provisioned directory containing the configured private PEM files and grant the container user read/traversal permissions. The SQL hostname must be reachable from the container; `localhost` refers to the container itself. Issuer, audience, and lifetimes retain base defaults unless overridden.

The image creates writable `/app/logs`; use a persistent volume for retained logs and separate storage per replica. `.dockerignore` excludes key/secret files, environment-specific settings, tests, migrations, and generated output. Keys are mounted at runtime, not baked into the image.

Valid database options and RSA keys are required before HTTP starts. A later SQL outage makes readiness fail while liveness remains available. The Dockerfile has no `HEALTHCHECK`; configure readiness probing in orchestration. No Docker Compose or CI/CD workflow is present in this submodule.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Startup rejects database configuration | Choose complete connection string or all split settings, not both; include server, database, and valid authentication |
| Startup rejects JWT configuration | Check key paths relative to content root, permissions, unencrypted private PEMs, minimum 2048-bit size, unique key IDs, exactly one active key, and lifetime ranges |
| Readiness is 503 | Check SQL reachability, database existence, credentials, and certificate trust; liveness does not test SQL |
| Readiness is healthy but account operations fail | Verify all V1–V4 migrations and stored procedures are present in the configured database |
| Protected endpoint returns 401 | Check Bearer header, RS256 signature, issuer/audience, expiry, and clocks; there is no clock-skew allowance |
| Admin endpoint returns 403 | Registration creates User accounts; the access token must carry Admin |
| Refresh/logout returns 401 | The token may be unknown, expired, or already revoked; refresh also requires an active account |
| HTTPS profile cannot start or browser rejects certificate | Check the local development certificate and availability of ports 7066/5116 |
| Browser cannot call the service across origins | No CORS middleware is configured here; confirm the parent application's gateway/proxy arrangement |
| Build cannot target the framework | Check `dotnet --list-sdks` for a .NET 9 SDK; this submodule does not pin an SDK patch |
| Container cannot read keys or write logs | Check runtime mounts and permissions for the non-root app user |

Environment-specific SQL/Flyway versions, infrastructure startup commands, key provisioning, gateway routing, and frontend integration must be confirmed in the parent application repository.
