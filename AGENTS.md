# AGENTS.md

## Project Overview

This repository contains the **Japanese Learning User Service**, responsible for user accounts, authentication, authorization, and user-related persistence for the Japanese Learning application.

The service follows a Clean Architecture style and should remain simple, secure, maintainable, testable, extensible, and production-ready.

When modifying the project, preserve the existing architecture and conventions unless the task explicitly requires a change.

---

## Tech Stack

Current primary technologies:

- .NET 9
- ASP.NET Core Web API
- MediatR
- FluentValidation
- SQL Server
- Dapper
- JWT Bearer Authentication
- ASP.NET Core Identity password hashing
- Flyway database migrations
- Swagger / OpenAPI
- .NET dependency injection
- .NET logging and health checks

The technology and persistence design may evolve as the project grows. Do not assume the current database schema or persistence implementation is final.

---

## Solution Structure

    src/
    ├── JapaneseLearning.User.Api
    ├── JapaneseLearning.User.Application
    ├── JapaneseLearning.User.Domain
    └── JapaneseLearning.User.Infrastructure

    tests/
    └── JapaneseLearning.User.UnitTests

    db/
    └── migration

### API

Responsibilities:

- HTTP endpoints and controllers
- Authentication and authorization configuration
- HTTP request/response concerns
- Global exception handling
- API response models
- Swagger/OpenAPI configuration
- Application startup and middleware

Controllers must remain thin.

Do not place business logic or database access in controllers.

### Application

Responsibilities:

- Application use cases
- Commands and queries
- MediatR handlers
- Validation
- Application abstractions/interfaces
- Application-level exceptions
- Pipeline behaviors
- Request/response models used by use cases

Application must not depend on Infrastructure.

### Domain

Responsibilities:

- Core domain entities
- Domain enums
- Domain rules and concepts

Domain should remain independent from API, Infrastructure, database, HTTP, and framework-specific implementation details whenever possible.

### Infrastructure

Responsibilities:

- SQL Server access
- Dapper repositories
- Security implementations
- Password hashing
- JWT/token generation
- Current authenticated user implementation
- Health checks
- External infrastructure integrations
- Configuration implementations

Infrastructure implements abstractions defined by Application where appropriate.

---

## Dependency Rules

Preserve the existing dependency direction.

    API
    ├── Application
    └── Infrastructure

    Infrastructure
    ├── Application
    └── Domain

    Application
    └── Domain

    Domain
    └── no project dependencies

Never introduce reverse dependencies such as:

- Domain -> Application
- Domain -> Infrastructure
- Domain -> API
- Application -> Infrastructure
- Application -> API

Do not bypass architectural boundaries for convenience.

---

## SOLID Principles

All new and modified code must follow SOLID principles where they improve the design.

### Single Responsibility Principle

A class or method should have one clear responsibility.

Avoid large handlers, services, controllers, repositories, or utility classes that perform unrelated work.

### Open/Closed Principle

Prefer designs that allow reasonable extension without repeatedly modifying unrelated existing code.

Do not introduce unnecessary abstractions solely to satisfy this principle.

### Liskov Substitution Principle

Implementations must respect the contracts defined by their abstractions.

Do not introduce implementations that behave unexpectedly compared with their interfaces.

### Interface Segregation Principle

Keep interfaces focused.

Do not create large interfaces containing unrelated operations.

### Dependency Inversion Principle

Business/application logic should depend on abstractions rather than infrastructure implementations.

Infrastructure should implement required abstractions.

Use dependency injection instead of manually constructing infrastructure dependencies inside application code.

---

## Clean Code

All code must be:

- Easy to read
- Easy to understand
- Easy to maintain
- Easy to test
- Easy to modify
- Reasonably easy to extend
- Production-ready

Prefer clarity over cleverness.

Use meaningful names that communicate intent.

Keep methods small and focused.

Keep classes cohesive.

Avoid:

- Duplicated logic
- Deeply nested conditions
- Unnecessary complexity
- Magic values
- Unclear abbreviations
- Giant classes or methods
- Premature optimization
- Speculative abstractions
- Unnecessary design patterns
- Over-engineering

Use early returns when they improve readability.

Extract methods or abstractions when they provide real readability, reuse, testability, or architectural value.

Do not create interfaces, wrappers, factories, services, or helper classes without a concrete reason.

Follow existing project conventions before introducing new ones.

Comments should explain important intent, constraints, or non-obvious decisions rather than compensate for unclear code.

---

## Feature Organization

Authentication features currently follow a feature-oriented structure such as:

    Auth/
    └── Register/
        ├── RegisterCommand.cs
        ├── RegisterCommandHandler.cs
        ├── RegisterCommandValidator.cs
        └── RegisterResponse.cs

When implementing a similar use case, inspect an existing feature first and follow its structure unless there is a strong reason not to.

Keep code belonging to a use case close together.

---

## MediatR

Use MediatR for application use cases consistent with the existing architecture.

Typical flow:

    Controller
        -> Command / Query
        -> ValidationBehavior
        -> LoggingBehavior
        -> Handler
        -> Application abstraction
        -> Infrastructure implementation

Controllers should send requests through `ISender` rather than directly orchestrating repositories or infrastructure services.

Handlers should coordinate the application use case.

Do not move HTTP-specific concerns into handlers.

Do not use MediatR merely to add unnecessary indirection outside the established application flow.

---

## Validation

Use FluentValidation for request/use-case validation consistent with the existing pipeline.

Validators should handle input rules such as:

- Required values
- Length limits
- Valid formats
- Simple structural constraints

Do not duplicate the same validation in controllers and handlers.

Business conditions requiring persistence or domain decisions belong in the appropriate handler/domain logic rather than basic FluentValidation rules.

Use the existing centralized validation pipeline.

---

## Exception Handling

Use the existing application exception hierarchy for expected application failures.

Examples include:

- `NotFoundException`
- `ConflictException`
- `UnauthorizedException`
- `ForbiddenException`

Allow the global exception handler to translate application exceptions into HTTP responses.

Do not add repetitive `try/catch` blocks to controllers or handlers when centralized exception handling already covers the case.

Do not expose stack traces, database errors, internal implementation details, or sensitive information to API clients.

Unexpected exceptions should result in a safe generic server error response while being logged appropriately.

---

## Async and Cancellation

All I/O operations should be asynchronous.

Propagate `CancellationToken` through the normal request flow:

    Controller
    -> MediatR
    -> Handler
    -> Repository / Infrastructure operation

Do not use `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` for normal asynchronous application flows.

Avoid blocking asynchronous operations.

---

## Persistence

The current persistence implementation uses:

- SQL Server
- Dapper
- Repositories
- Stored procedures
- Flyway migrations

Repository abstractions belong in Application.

Repository implementations belong in Infrastructure.

Keep SQL/database concerns out of controllers, handlers, and Domain.

Use parameterized database operations. Never construct SQL using untrusted input through string concatenation or interpolation.

Use transactions when multiple database operations must succeed or fail atomically.

Avoid unnecessary database round trips.

### Database Evolution

The current database design is **not considered final**.

The schema, stored procedures, repository design, and persistence approach may evolve as new features are added.

For current tasks:

- Follow the existing persistence conventions
- Do not assume the existing schema is immutable
- Do not redesign persistence without a requirement
- Do not change database technology without explicit instruction
- Do not perform unrelated schema changes
- Keep migrations forward-only and reproducible

Database schema changes must be represented through migrations rather than undocumented manual database changes.

Do not modify an existing migration that may already have been applied unless explicitly instructed and the consequences are understood.

Prefer creating a new migration for schema evolution.

---

## Security

Treat authentication and authorization code as security-sensitive.

Never:

- Store plaintext passwords
- Log passwords
- Log JWT secrets
- Log access tokens
- Log refresh tokens
- Commit secrets
- Hard-code production credentials
- Expose password hashes through APIs

Use the existing password hashing abstraction.

Use cryptographically secure randomness for security tokens.

Refresh tokens should be stored and compared using their hash according to the existing design.

JWT configuration must come from configuration or environment sources rather than hard-coded production values.

Authorization must be enforced server-side.

Do not trust role, user ID, email, or authorization information supplied by the client when authenticated claims are the authoritative source.

Do not weaken authentication, token validation, password handling, or authorization rules merely to make a test pass.

---

## Logging

Use structured logging.

Prefer structured placeholders such as:

    logger.LogInformation(
        "Handled {RequestName} in {ElapsedMilliseconds}ms",
        requestName,
        elapsedMilliseconds);

Avoid interpolated log strings when structured logging is appropriate.

Logs should provide useful operational context without leaking sensitive information.

Avoid excessive logging in frequently executed paths.

Never log secrets, credentials, passwords, raw tokens, or other sensitive authentication data.

---

## API Design

Keep API contracts clear and predictable.

Use appropriate HTTP status codes.

Preserve existing API contracts unless the task explicitly requires a breaking change.

Keep HTTP-specific concerns in the API layer.

When adding or changing endpoints, keep Swagger/OpenAPI metadata accurate where applicable.

Do not expose persistence entities directly when doing so unnecessarily couples the public API to the database or domain representation.

---

## Production Readiness

Code should be written as production code, not tutorial or prototype code.

Consider where relevant:

- Validation
- Error handling
- Security
- Cancellation
- Concurrency
- Transaction boundaries
- Nullability
- Logging
- Observability
- Performance
- API compatibility
- Database consistency
- Failure scenarios

Do not add infrastructure for hypothetical problems.

Production-ready does not mean maximum complexity.

Choose the simplest design that correctly handles the real requirements.

---

## Testing

Behavior changes should include appropriate tests when practical.

Prioritize tests for:

- Application handlers
- Validators
- Business rules
- Security-sensitive behavior
- Bug fixes
- Important edge cases

A bug fix should normally include a regression test when feasible.

Test observable behavior rather than private implementation details.

Mock architectural boundaries where appropriate rather than mocking every internal method.

Tests should remain deterministic and independent.

Do not weaken or delete valid tests simply to make a change pass.

---

## Change Scope

Make the smallest coherent change required to complete the task correctly.

Do not:

- Refactor unrelated code
- Rename unrelated files
- Reformat unrelated files
- Change architecture unnecessarily
- Upgrade packages without a reason
- Change public contracts unnecessarily
- Introduce unrelated features

If an existing issue is discovered outside the requested scope, mention it instead of automatically fixing it.

---

## Repository Exploration

Before implementing a change:

1. Read this `AGENTS.md`.
2. Identify the layer and feature affected.
3. Inspect the directly relevant files.
4. Inspect one similar existing implementation when useful.
5. Understand the existing convention.
6. Make the smallest correct change.

Do not scan the entire repository unless the task genuinely requires broad analysis.

Ignore generated/build artifacts during normal code exploration:

    .vs/
    bin/
    obj/
    TestResults/
    coverage/
    CoverageReport/
    logs/

Do not edit generated files.

Prefer source files and project configuration over generated output.

---

## Implementation Decisions

When multiple solutions are valid, prefer in this order:

1. Correctness
2. Security
3. Existing project conventions
4. Simplicity
5. Readability
6. Maintainability
7. Testability
8. Extensibility
9. Performance when relevant

Do not sacrifice readability and maintainability for insignificant optimization.

Before creating a new abstraction, check whether the project already has an appropriate abstraction or pattern.

Before adding a dependency, determine whether the requirement can reasonably be implemented with the existing stack.

---

## Build and Verification

After code changes, run the narrowest useful verification first.

Typical commands:

    dotnet restore
    dotnet build JapaneseLearning.User.sln
    dotnet test JapaneseLearning.User.sln

For small changes, targeted project/test execution is acceptable before running broader verification.

Do not claim that code builds or tests pass unless the relevant command was actually executed successfully.

If verification cannot be run, clearly state what was not verified.

---

## Definition of Done

A task is complete when applicable requirements are satisfied:

- Requested behavior is implemented
- Architecture boundaries are preserved
- SOLID principles are respected
- Clean Code principles are respected
- Code is readable and understandable
- Code is maintainable and reasonably extensible
- Security requirements are preserved
- Validation and error handling are appropriate
- Async operations remain non-blocking
- Cancellation is propagated where applicable
- Database changes are migration-driven when required
- Relevant tests are added or updated
- Relevant build/tests pass
- No secrets are introduced
- No unrelated changes are included
- Implementation is suitable for production use

---

## Final Rule

Do not blindly apply generic best practices.

First understand how this repository currently solves similar problems, then implement the requested change using the simplest clean solution consistent with the architecture.

Prefer focused, understandable, maintainable, extensible, production-ready code over clever or over-engineered code.