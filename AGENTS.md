# AGENTS.md

## Project context

- This .NET service owns authentication, user identity, roles, and user persistence for the Japanese Learning application. Angular owns the frontend; Quarkus owns vocabulary, flashcards, and the API Gateway.
- Stack: .NET 9 / ASP.NET Core controllers, MediatR, FluentValidation, SQL Server, Dapper/stored procedures, Identity password hashing, RS256 JWT/JWKS, Serilog, and prometheus-net.
- Preserve the existing four-layer architecture. Use [README.md](README.md) for setup, configuration, endpoint details, and deployment; verify task-relevant claims against code.

## Repository navigation

All project names below have the prefix `JapaneseLearning.User.`.

| Location | Look here for |
| --- | --- |
| `JapaneseLearning.User.sln` | Four service projects and the test project |
| `src/JapaneseLearning.User.Api/` | `Controllers/`, `Authentication/`, `Program.cs`, `appsettings*.json` |
| API `Common/`, `Health/`, `Observability/` | Errors/responses, safe logging, correlation, probes, metrics |
| `src/JapaneseLearning.User.Application/` | `Auth/{Register,Login,Refresh,Logout}/` commands, handlers, validators, response records |
| Application `Abstractions/`, `Common/` | Persistence/security interfaces, behaviors, application exceptions |
| `src/JapaneseLearning.User.Domain/Entities/` | User, refresh token, and role definitions |
| `src/JapaneseLearning.User.Infrastructure/` | `Persistence/Repositories/`, `Database/`, `Security/`, `Configuration/`, `HealthChecks/` |
| `db/migration/` | Versioned SQL scripts; Flyway runner is external |
| `tests/JapaneseLearning.User.UnitTests/` | `Security/`, `Logging/`, `Health/`, `Observability/` |
| `Dockerfile`, `.dockerignore` | Container build and context exclusions |

## Token-efficient exploration

- Start from the user's task; identify the smallest relevant feature/layer before reading code.
- Inspect filenames with scoped `rg --files`, then use targeted `rg -n` searches and bounded reads. Do not scan the whole repository by default.
- Read the relevant implementation, direct dependencies, and one similar feature/test when useful. Expand only when evidence requires it.
- Reuse this file, README, and already examined context; do not repeatedly dump files or read generated output for orientation.
- Exclude `.git/`, `.vs/`, `bin/`, `obj/`, `TestResults/`, `coverage/`, `CoverageReport/`, `logs/`, and dependency directories from normal searches.
- Do not open key files, secret stores, or credential-bearing local configuration merely to inspect configuration structure.
- Batch independent targeted reads; avoid redundant commands and tools. Never save tokens by skipping necessary validation or security checks.

## Existing coding conventions

- Follow the existing repository style and any applicable `.editorconfig`; none is currently checked into this submodule. Do not introduce a new style or formatter configuration.
- C# uses file-scoped namespaces, normally matching the project/feature; inspect existing namespaces before adding imports because some paths and namespaces differ.
- Use PascalCase types, members, and methods; `I`-prefixed interfaces; camelCase locals/parameters; `_camelCase` explicit private fields; `Async` suffixes for asynchronous operations except framework contracts such as `Handle`.
- Nullable references and implicit usings are enabled. Honor nullability rather than suppressing warnings to bypass a real condition.
- C# generally uses four-space indentation and braces on separate lines. Preserve the nearby style, including existing constructor/primary-constructor choices and project-file indentation.
- Commands and use-case responses are typically sealed positional records; handlers, validators, repositories, and controllers are generally sealed classes.
- Keep a use case together under `Application/Auth/<Feature>/`: command, handler, validator when needed, and response. Follow Register/Login as examples.
- Inject dependencies through constructors; register through existing `DependencyInjection.cs` extensions or the API composition root. Do not manually construct infrastructure in handlers.
- Controllers use route attributes and `ISender` for application use cases; claim-only endpoints use `ICurrentUser`. Keep transport logic in API.
- Comments explain intent or constraints, not obvious syntax. Do not normalize unrelated existing formatting.

## Clean code and SOLID

- Apply DRY, KISS, YAGNI, and SOLID to concrete needs: focused methods, cohesive classes, descriptive names, explicit dependencies, and readable control flow.
- Keep business decisions separate from HTTP/database mechanics. Use guard clauses to reduce nesting and named values for meaningful constants.
- Prefer composition when appropriate; implementations must honor interface contracts, and interfaces must remain focused.
- Do not create an interface, factory, wrapper, base class, or generic helper without a concrete reuse, boundary, or testability need.
- Prioritize correctness, security, existing conventions, simplicity, readability, and maintainability over cleverness or speculative optimization.

## Reuse and consistency

- Before adding behavior, search the affected layer for an existing abstraction, validator, DTO, or implementation that fits.
- Extend or reuse cohesive components; do not duplicate business rules or move unrelated responsibilities into a shared utility.
- Keep reusable application logic in Application and implementation details in Infrastructure. Do not leak persistence entities into public contracts unnecessarily.
- Preserve public routes, DTO fields, status codes, and claim contracts unless the requested change requires otherwise.
- Use the current stack before adding a dependency; justify any necessary package or architectural addition.

## Architecture and dependencies

- Project references: API → Application + Infrastructure; Infrastructure → Application + Domain; Application → Domain; Domain → no projects.
- Never introduce reverse/circular dependencies. Keep Domain independent of HTTP, database access, and infrastructure/framework implementation details.
- Application handlers coordinate use cases through abstractions; repository/security interfaces belong in Application.
- Infrastructure implements persistence/security abstractions; API owns authentication setup, HTTP responses, middleware, and composition.
- Existing pipeline order is controller → MediatR → ValidationBehavior → LoggingBehavior → handler. Do not duplicate pipeline validation in controllers.
- Put structural/input rules in FluentValidation; persistence-dependent business conditions belong in handlers or domain logic.
- Preserve service boundaries. Do not add frontend, vocabulary, or gateway responsibilities or new patterns without a task requirement.

## Production-quality implementation

- Validate untrusted input; handle relevant nulls, expected failures, cancellation, and resource disposal without speculative defensive layers.
- Use asynchronous I/O and propagate `CancellationToken` through controller, MediatR, and repository operations. Do not block with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`.
- Use existing application exceptions (`NotFoundException`, `ConflictException`, `UnauthorizedException`, `ForbiddenException`); let global handling translate them. Avoid repetitive catch/rethrow blocks.
- Preserve actual responses: auth success returns DTOs directly; logout is 204; errors use `ApiResponse<T>`. Do not automatically wrap successes. Keep Swagger metadata accurate.
- Use parameterized Dapper `CommandDefinition` calls and stored procedures; never interpolate untrusted SQL. Avoid unnecessary round trips.
- Use a transaction/atomic operation when a required multi-write invariant needs it; do not assume separate repository calls share a transaction.
- Schema/procedure changes require new forward-only, reproducible migrations in `db/migration/`. Do not edit potentially applied migrations without explicit instruction and understood consequences.
- The schema may evolve; follow current persistence conventions without treating them as immutable or redesigning them without a requirement.
- Use typed options and external configuration; preserve startup validation. Never hard-code environment-specific deployment values.
- Use structured `ILogger<T>` templates. Preserve safe exception diagnostics, correlation, and existing event conventions; avoid duplicate error logs or logging whole request/response objects.
- Preserve health/metrics contracts and bounded metric labels; do not add user IDs, tokens, or arbitrary paths as metric labels.

## Authentication and security

- Use `IPasswordHasher` and its Identity implementation; never store/expose plaintext passwords or expose hashes through APIs.
- Preserve RS256 signing and validation of signature, algorithm, issuer, audience, required expiry, and zero clock skew.
- RSA keys load once at startup from configured PEM paths; require a matching pair of at least 2048 bits. JWKS exposes public parameters only.
- Preserve JWT claims and standard inbound mapping used by `ICurrentUser`. Use authenticated claims, not client-supplied IDs/roles, as authorization authority.
- Registration creates active User accounts. Login/refresh check active status; protected endpoints use bearer authentication and Admin role authorization where declared.
- Refresh tokens use cryptographic randomness and SHA-256 hash storage. Preserve expiry, rotation, and revocation checks; logout revokes the supplied refresh token, not issued access tokens.
- Current refresh rotation uses separate calls and is not atomic; do not infer replay/concurrency guarantees. Review races and failure paths when changing this flow.
- Never log credentials, password hashes, access/refresh tokens, Authorization/cookie headers, private keys, credential-bearing connection strings, or authentication payloads.
- Keep secrets outside source/image layers; do not expose internal exception details to clients. Safe logging is not a general-purpose secret scanner.
- Never bypass or weaken validation, token checks, authorization, or secure error handling to simplify implementation or make tests pass.
- For security changes, test both permitted behavior and rejection paths; explicitly assess expiry, revoked/inactive states, claims, and concurrency where relevant.

## Testing and verification

- Add/update behavior tests for changes to handlers, validation, business rules, and security. Bug fixes normally require a regression test; documentation-only edits need document checks.
- Follow xUnit `[Fact]`/`[Theory]`, descriptive PascalCase test names, and Moq boundary mocks. Test observable behavior, not private structure; avoid redundant tests.
- Cover success, expected failures, and relevant edges. Keep tests independent and deterministic; never weaken valid assertions to obtain a pass.
- Reuse disposable RSA fixtures and existing test hosts. Some tests use ephemeral loopback ports/temp files; SQL dependencies are mocked. No live database integration suite exists here.
- Run the narrowest useful tests first, then broader relevant checks for cross-cutting changes. Do not repeat passing checks without a new reason.
- Commands from the repository root:

```powershell
dotnet test tests/JapaneseLearning.User.UnitTests/JapaneseLearning.User.UnitTests.csproj --filter "FullyQualifiedName~RelevantTestClass"
dotnet build JapaneseLearning.User.sln
dotnet test JapaneseLearning.User.sln
```

- Replace the filter placeholder with the actual class. Use `dotnet restore JapaneseLearning.User.sln` when restore is needed and allowed; do not use `--no-build` against stale outputs.
- For documentation-only work, verify paths, commands, and claims against source and run `git diff --check`; do not start infrastructure or apply migrations just to verify documentation.
- Report exactly what ran and its outcome. Never claim an unexecuted build/test passed; state blocked or omitted verification and why.

## Minimal change policy

- Modify only files needed for the task. Preserve unrelated working-tree changes; inspect `git status --short` before editing.
- No unrelated refactoring, renaming, formatting, package upgrades, schema changes, or documentation files.
- Do not change authentication behavior or public contracts incidentally. Prefer a small, coherent, reviewable diff.
- Mention out-of-scope issues instead of silently fixing them. Never edit generated/build artifacts.

## AI agent workflow

1. Understand the request and constraints; clarify ambiguous/high-impact requirements before risky assumptions. Proceed autonomously on straightforward tasks.
2. Identify the relevant modules, inspect local instructions and current changes, and read targeted implementation/convention examples.
3. Reuse existing components and implement the smallest correct solution within the established boundaries.
4. Add/update relevant tests and run appropriate verification.
5. Review the diff for correctness, secrets, compatibility, and unrelated changes.
6. Summarize the behavior change, verification, and remaining limitations concisely.

## Communication and token efficiency

- Be concise and technically precise; do not repeat the request or explain basic .NET concepts unless asked.
- Give meaningful updates about findings, decisions, and blockers rather than narrating every command.
- Show only relevant snippets; summarize behavior rather than entire files. Mention tradeoffs only when material.
- Clearly distinguish verified results from assumptions and unresolved issues. Efficiency must not reduce correctness, maintainability, or security.

## Prohibited practices

- Do not invent files/features, duplicate existing business logic, overengineer requirements, or introduce unrelated architectural patterns.
- Do not silently swallow exceptions; handle expected failures intentionally through established paths.
- Do not hard-code secrets, bypass safeguards, change contracts without task justification, or report fabricated verification.
- Do not perform destructive operations without explicit approval.