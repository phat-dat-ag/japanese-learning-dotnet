# Japanese Learning - User Service

User Service for the Japanese Learning application.

This service is responsible for user-related functionality and is built with ASP.NET Core Web API following a layered architecture.

---

## Tech Stack

- .NET 9
- ASP.NET Core Web API
- C#
- SQL Server
- Dapper
- MediatR
- FluentValidation
- Swagger / OpenAPI
- Health Checks
- xUnit

---

## Project Structure

```text
japanese-learning-user/
│
├── src/
│   ├── JapaneseLearning.User.Api/
│   │   ├── Common/
│   │   ├── Properties/
│   │   └── Program.cs
│   │
│   ├── JapaneseLearning.User.Application/
│   │   ├── Common/
│   │   │   └── Behaviors/
│   │   └── DependencyInjection.cs
│   │
│   ├── JapaneseLearning.User.Domain/
│   │
│   └── JapaneseLearning.User.Infrastructure/
│       ├── Database/
│       ├── HealthChecks/
│       └── DependencyInjection.cs
│
├── tests/
│   └── JapaneseLearning.User.UnitTests/
│
├── JapaneseLearning.User.sln
└── README.md
```

---

## Prerequisites

Before running the project, make sure the following are installed:

- Git
- .NET 9 SDK
- SQL Server

Verify .NET:

```bash
dotnet --version
```

The project targets:

```text
net9.0
```

---

## Getting Started

### 1. Clone the Repository

```bash
git clone <repository-url>
cd japanese-learning-user
```

Replace `<repository-url>` with the repository URL.

---

## Database Configuration

The application requires a SQL Server connection string.

The configuration key is:

```text
Database:ConnectionString
```

Example:

```text
Server=localhost,1433;Database=JapaneseLearningUser;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True
```

Replace `YOUR_PASSWORD` with the actual SQL Server password.

**Do not commit database credentials or passwords to the repository.**

### Windows Environment Variable

You can configure the connection string using:

```cmd
setx Database__ConnectionString "Server=localhost,1433;Database=JapaneseLearningUser;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True"
```

After running `setx`, open a new terminal.

Verify:

```cmd
echo %Database__ConnectionString%
```

---

## JWT RSA Configuration

Access tokens use RS256. The parent orchestration repository owns the RSA key
pair; this service only reads PEM files. Do not copy or commit key material here.

Configure these settings through the existing `Jwt` options section (JSON,
environment variables, or .NET user secrets):

| Setting | Purpose |
| --- | --- |
| `Jwt:PrivateKeyPath` | Unencrypted RSA private PEM file used to sign access tokens |
| `Jwt:PublicKeyPath` | Corresponding RSA public PEM file used to validate access tokens |
| `Jwt:KeyId` | Non-empty identifier emitted as the JWT header `kid` |
| `Jwt:Issuer` | Expected issuer; default `JapaneseLearning.User` |
| `Jwt:Audience` | Expected audience; default `JapaneseLearning` |
| `Jwt:AccessTokenExpirationMinutes` | Positive access-token lifetime; default 15 |
| `Jwt:RefreshTokenExpirationDays` | Existing refresh-token lifetime; unchanged, default 7 |

`Jwt:Secret` is no longer used. Base settings leave key paths and ID empty so a
non-development deployment must supply them. Development settings use
`../../../secrets/jwt/private.pem`, `../../../secrets/jwt/public.pem`, and key ID
`japanese-learning-local-1`. Relative paths are resolved against the application's
content root (`src/JapaneseLearning.User.Api` during normal local development),
not the process working directory. With the current checkout these resolve to:

- `C:\Personal\japanese-learning\secrets\jwt\private.pem`
- `C:\Personal\japanese-learning\secrets\jwt\public.pem`

For a different content root or deployment layout, override the paths. For example,
container environment variables can point directly to mounted files:

```text
Jwt__PrivateKeyPath=/app/secrets/jwt/private.pem
Jwt__PublicKeyPath=/app/secrets/jwt/public.pem
Jwt__KeyId=japanese-learning-1
Jwt__Issuer=JapaneseLearning.User
Jwt__Audience=JapaneseLearning
Jwt__AccessTokenExpirationMinutes=15
```

The application identity must be able to read both files. The loader reads them
asynchronously once during startup and imports PEM with `RSA.ImportFromPem`.
PKCS#8/PKCS#1 private keys and SubjectPublicKeyInfo/PKCS#1 public keys are supported.
Keys must be at least 2048 bits and form a matching pair. Missing paths or key ID,
unreadable files, invalid/encrypted PEM, and mismatched keys fail startup with an
options/configuration error. Errors do not include PEM contents.

Signing uses private RSA parameters and sets `RsaSecurityKey.KeyId`; the JWT
handler writes that value to `kid`. Bearer authentication receives only public
parameters and allows only RS256, retaining signature, issuer, audience, and
lifetime checks with zero clock skew. Claims and .NET name/role mapping are
unchanged. Refresh-token generation, hashing, persistence, and rotation are
unchanged. Restart the service after replacing key files or changing key metadata.
Previously issued HS256 access tokens are rejected; clients can obtain RS256
access tokens through login or an existing valid refresh token.

### Public signing key (JWKS)

`GET /.well-known/jwks.json` is anonymous and returns one RSA public signing key
in a standard `keys` array. Its fields are `kty: RSA`, `use: sig`, `alg: RS256`,
`kid`, `n`, and `e`. The key ID matches newly issued JWT headers. Modulus `n` and
exponent `e` use unpadded Base64Url encoding.

The endpoint reuses the in-memory public validation key loaded at startup. It
does not read PEM files per request or expose private parameters, paths, or other
configuration. No additional configuration is required. This is a JWKS endpoint;
OpenID Connect discovery and key rotation are not implemented.

With the HTTPS development profile running, fetch it without Authorization:

```powershell
Invoke-RestMethod https://localhost:7066/.well-known/jwks.json | ConvertTo-Json -Depth 3
```

Log in using the existing API and compare the access-token header `kid` with the
returned key's `kid`. A verifier can Base64Url-decode `n` and `e` into the RSA
modulus and exponent and verify the RS256 signature, while still enforcing the
expected issuer, audience, and lifetime. `JwksEndpointTests` automates this check
over HTTP using the production key loader and TokenService with the existing
temporary test-key fixture.

### Manual authentication check

1. Configure the existing database connection and make the parent-owned PEM files
   readable. Run `dotnet run --project src/JapaneseLearning.User.Api --launch-profile https`.
   If another instance occupies its ports or locks Debug outputs, stop that instance first.
2. Open `https://localhost:7066/swagger`. Register a test account with
   `POST /api/auth/register` (`username`, `email`, `password`), then log in with
   `POST /api/auth/login` (`email`, `password`).
3. Inspect the access token locally: header `alg` must be `RS256`, `kid` must match
   configuration, and payload must retain `sub`, `unique_name`, `email`, `role`,
   `jti`, `exp`, `iss`, and `aud`. Log in again and check that `jti` differs.
4. Use Swagger's Authorize control with the access token. `GET /api/auth/me`
   should return the authenticated account. `GET /api/auth/admin-test` should
   return 403 for a User and 200 for an existing Admin account.
5. Call `POST /api/auth/refresh` with `{ "refreshToken": "<refresh token>" }`.
   Verify the new access token works for `/me`, and the rotated-out refresh token
   is rejected. Call `POST /api/auth/logout` with the new refresh token; subsequent
   refresh with that token should fail. Logout preserves the existing behavior:
   an access token already issued remains valid until expiration.
6. An altered or expired access token must return 401 from `/me`. To check expiry
   quickly, set `Jwt__AccessTokenExpirationMinutes=1`, restart, log in, and retry
   after expiry. Automated tests additionally check wrong issuer, audience, key,
   unsigned tokens, missing expiration, and disallowed algorithms.
7. Set either key path to a nonexistent file and restart: startup must fail before
   requests are served. Restore configuration afterward.

The RSA tests use disposable keys in the operating system's temporary directory;
they do not read, replace, or generate deployment keys in this repository.

---

## Container

Build the Linux image from this repository root:

```bash
docker build -t japanese-learning-user:local .
```

The image uses official .NET 9 SDK restore/build/publish stages and an ASP.NET
Core 9 runtime stage containing only published API output. It runs as the
image's non-root `app` user (`APP_UID`, UID 1654), in Production, listening on
HTTP port **8080** on all interfaces. Publish that port through orchestration.
`Http__RedirectToHttps=false` disables application HTTPS redirection in this
image; TLS belongs at the gateway. Other deployments retain redirection by default.

Supply configuration at runtime using standard ASP.NET Core environment variables:

| Variable | Value |
| --- | --- |
| `Database__ConnectionString` | SQL Server connection string using a hostname reachable from the container, not `localhost` |
| `Jwt__Issuer` | Expected token issuer |
| `Jwt__Audience` | Expected token audience |
| `Jwt__PrivateKeyPath` | `/app/secrets/jwt/private.pem` |
| `Jwt__PublicKeyPath` | `/app/secrets/jwt/public.pem` |
| `Jwt__KeyId` | Signing key ID shared with verifiers |
| `Jwt__AccessTokenExpirationMinutes` | Positive lifetime in minutes (default 15) |

Issuer and audience retain the defaults documented above if omitted.
`Jwt__RefreshTokenExpirationDays` can also override the existing default of 7.
Do not supply credentials or key contents as image build arguments.

The root orchestration repository owns `secrets/jwt/private.pem` and
`secrets/jwt/public.pem`. Mount them read-only at the corresponding `/app/secrets/jwt/`
paths above and grant UID 1654 read access and directory traversal permissions.
Never copy or generate deployment keys in this repository or during the image build.
The build context excludes PEM files, secret directories, and local configuration.
Restart the container after replacing keys; existing startup validation and RS256
validation remain enforced. The public key is available at `GET /.well-known/jwks.json`.

The existing anonymous `GET /health` checks SQL Server connectivity: HTTP 200
when healthy, HTTP 503 when unavailable. Use it as a readiness probe; it does not
verify migration versions. Configure probing in orchestration; no probe utilities
or Docker HEALTHCHECK are installed in the runtime image.

Run migrations first through the root Compose Flyway service using `db/migration`.
This image neither includes nor executes Flyway. Compose configuration belongs
in the root orchestration repository.

---

## Database Requirements

The application expects the following database:

```text
JapaneseLearningUser
```

Make sure:

- SQL Server is running.
- `JapaneseLearningUser` exists.
- The configured user has access to the database.
- The connection string is correct.
- SQL Server is accessible from the application.

---

## Build

Restore dependencies:

```bash
dotnet restore
```

Build the solution:

```bash
dotnet build JapaneseLearning.User.sln
```

Expected result:

```text
Build succeeded.
```

---

## Run

Start the API:

```bash
dotnet run --project src/JapaneseLearning.User.Api
```

The default development endpoint is:

```text
http://localhost:5116
```

---

## Verify

### Swagger

Open:

http://localhost:5116/swagger

Swagger should load successfully.

### Health Check

Open:

http://localhost:5116/health

The health check should succeed when the application can connect to SQL Server.

---

## Architecture

The project follows a layered architecture:

```text
API
 │
 ▼
Application
 │
 ▼
Domain

Infrastructure
 ├── Database
 ├── Health Checks
 └── External Implementations
```

### API

Responsible for:

- HTTP endpoints
- API responses
- Exception handling
- Swagger
- Health checks
- HTTP-specific concerns

### Application

Responsible for:

- Application use cases
- MediatR
- Validation
- Pipeline behaviors
- Application services

Current MediatR pipeline:

```text
Request
   ↓
LoggingBehavior
   ↓
ValidationBehavior
   ↓
Handler
```

### Domain

Contains:

- Domain entities
- Business rules
- Core domain concepts

The Domain layer should remain independent from Infrastructure and framework-specific implementation details.

### Infrastructure

Responsible for:

- SQL Server
- Dapper
- Database connections
- Health checks
- Infrastructure configuration

---

## Error Handling

The API uses a global exception handler and a consistent response format.

Example:

```json
{
  "success": false,
  "data": null,
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "One or more validation errors occurred.",
    "details": []
  },
  "traceId": "0H..."
}
```

The `traceId` can be used to correlate an API error with application logs.

Unexpected exceptions are handled centrally and should not expose sensitive implementation details to clients in production.

---

## API Response Format

Successful responses use the common API response structure:

```json
{
  "success": true,
  "data": {},
  "error": null,
  "traceId": "0H..."
}
```

Error responses use:

```json
{
  "success": false,
  "data": null,
  "error": {
    "code": "ERROR_CODE",
    "message": "Error message",
    "details": null
  },
  "traceId": "0H..."
}
```

---

## Logging

Application requests are logged through the MediatR logging pipeline.

The logging behavior records:

- Request name
- Execution time
- Successful requests
- Failed requests

Example:

```text
Handling CreateUserCommand
Handled CreateUserCommand in 25ms
```

Failed requests include the exception and execution time in the logs.

---

## Validation

Request validation is implemented using FluentValidation.

Validation runs through the MediatR pipeline before the request handler.

```text
API Request
    ↓
MediatR
    ↓
ValidationBehavior
    ↓
Validator
    ↓
Handler
```

If validation fails, the request handler is not executed.

---

## Testing

Run all tests:

```bash
dotnet test JapaneseLearning.User.sln
```

Unit tests are located in:

```text
tests/JapaneseLearning.User.UnitTests/
```

Tests should be added for meaningful business and application behavior.

Temporary test controllers or endpoints should be removed after development verification.

---

## Development Workflow

Create a feature branch from `develop`:

```bash
git switch develop
git pull
git switch -c feature/<feature-name>
```

Example:

```bash
git switch -c feature/user-registration
```

Before creating a Pull Request:

```bash
dotnet build JapaneseLearning.User.sln
dotnet test JapaneseLearning.User.sln
```

Review changes:

```bash
git status
git diff
```

Commit changes:

```bash
git add .
git commit -m "feat: implement user registration"
```

Push the feature branch:

```bash
git push -u origin feature/user-registration
```

Create the Pull Request with:

```text
Base: develop
Compare: feature/user-registration
```

---

## Development Rules

- Do not commit secrets or database passwords.
- Do not hard-code connection strings.
- Keep business logic out of controllers.
- Keep the Domain layer independent from Infrastructure.
- Put use-case logic in Application.
- Put database implementation in Infrastructure.
- Use the common API response format.
- Use the global exception handler.
- Add tests for meaningful application behavior.
- Remove temporary development code after verification.

---

## Common Commands

Restore dependencies:

```bash
dotnet restore
```

Build solution:

```bash
dotnet build JapaneseLearning.User.sln
```

Run tests:

```bash
dotnet test JapaneseLearning.User.sln
```

Run API:

```bash
dotnet run --project src/JapaneseLearning.User.Api
```

Check Git status:

```bash
git status
```

---

## Current Status

The project foundation is set up and ready for feature development.

Currently configured:

- Layered architecture
- SQL Server connection
- Dapper
- Database connection factory
- SQL Server health check
- Configuration Options
- MediatR
- MediatR validation pipeline
- MediatR logging pipeline
- Global exception handling
- Common API response
- Swagger / OpenAPI
- Development environment configuration
- Unit test project