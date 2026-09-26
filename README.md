# dotnet-microservices-tutorial

A complete tutorial for building a **.NET microservices architecture** with **ASP.NET Core 10** (.NET 10 LTS, C# 13) and **.NET Aspire**: local orchestration, service discovery, an API gateway, synchronous and asynchronous inter-service communication, a choreographed Saga, distributed tracing, resilience, and consumer-driven contract testing, across five independently deployable services plus a gateway.

This is a **monorepo**: one Git repository, one project per microservice, each with its own `.csproj` and its own deployment lifecycle - not a shared multi-project library the way a single ASP.NET Core solution normally is.

This document is the **complete specification** of the project: it is meant to be followed step by step to implement each branch.

## Table of contents

- [What microservices actually change, and why this decomposition](#what-microservices-actually-change-and-why-this-decomposition)
- [Microservices in .NET: the ecosystem, and where this project fits](#microservices-in-net-the-ecosystem-and-where-this-project-fits)
- [Service decomposition](#service-decomposition)
- [Why `Users` (identity) and `Customers` (e-commerce) stay in separate services](#why-users-identity-and-customers-e-commerce-stay-in-separate-services)
- [Scope: what this tutorial covers, and what it deliberately leaves out](#scope-what-this-tutorial-covers-and-what-it-deliberately-leaves-out)
- [Tech stack](#tech-stack)
- [Architecture overview](#architecture-overview)
- [Load balancing and service discovery](#load-balancing-and-service-discovery)
- [Data model](#data-model)
- [Common.Lib: what belongs in a shared project, and what never does](#commonlib-what-belongs-in-a-shared-project-and-what-never-does)
- [Design patterns used](#design-patterns-used)
  - [The Saga in detail: order confirmation](#the-saga-in-detail-order-confirmation)
- [Branching strategy](#branching-strategy)
- [Repository structure](#repository-structure)
- [Standard response format](#standard-response-format)
- [Testing strategy](#testing-strategy)
  - [Test naming convention](#test-naming-convention)
- [feature/common-lib](#featurecommon-lib)
- [feature/infrastructure](#featureinfrastructure)
- [feature/identity-api](#featureidentity-api)
- [feature/catalog-api](#featurecatalog-api)
- [feature/customer-api](#featurecustomer-api)
- [feature/order-api](#featureorder-api)
- [feature/notification-worker](#featurenotification-worker)
- [feature/api-gateway](#featureapi-gateway)
- [feature/observability](#featureobservability)
- [feature/resilience](#featureresilience)
- [feature/contract-testing (bonus)](#featurecontract-testing-bonus)
- [Order of work](#order-of-work)
- [Code conventions](#code-conventions)
- [Concepts covered](#concepts-covered)
- [How to follow this tutorial](#how-to-follow-this-tutorial)

## What microservices actually change, and why this decomposition

Splitting a system into services isn't primarily about the boxes on a diagram - it's about **what a single deployment unit is allowed to know about**. A monolith can join across every table because they all live in one process, one transaction, one database connection. A microservice can't: the moment `catalog-api` and `order-api` are separate deployables with separate databases, a query that used to be a SQL `JOIN` becomes a network call, a schema change that used to be one migration becomes a coordinated rollout, and a business rule that used to run inside one transaction becomes several services agreeing with each other after the fact rather than during.

This tutorial is built around five services, each carved out where a real boundary already exists in the domain - not split for the sake of having several projects:

- **`identity-api`** and **`customer-api`** answer genuinely different questions (*can this request authenticate* versus *who is this business-wise*) and change for different reasons, at a different pace, driven by different concerns
- **`catalog-api`** is read far more often than it's written, and scales independently of everything else
- **`order-api`** is the one place business rules actually cross service lines (it needs a valid customer and a valid product to exist before it can do anything), which is exactly why it's the service that demonstrates synchronous calls, idempotency, and a Saga
- **`notification-worker`** has no state of its own at all - it exists purely to react to what happens elsewhere, which is why it's the tutorial's only fully event-driven, database-less service

Every concept below - service discovery, an API gateway, a Saga instead of a distributed transaction - exists to answer a problem this five-way split actually creates, not as boilerplate copied because "microservices projects have these."

## Microservices in .NET: the ecosystem, and where this project fits

Unlike, say, the JVM world where the tooling for this problem space converged years ago, .NET's microservices tooling has gone through a real shift recently, and it's worth naming the current landscape honestly before explaining this project's specific choices:

- **.NET Aspire** - Microsoft's own, first-party answer, and by 2026 the default recommendation for new .NET distributed applications. An `AppHost` project declares every service and its dependencies in C#, and Aspire handles local orchestration, logical-name service discovery, an OpenTelemetry dashboard, and Polly-based resilience out of the box, with no separate registry process to run. As of Aspire 13.0, orchestration is no longer even .NET-only - it can coordinate Python and JavaScript services in the same dashboard.
- **Dapr** - a sidecar-based distributed application runtime (pub/sub, state management, service invocation, bindings), donated to the CNCF, framework-agnostic by design. It's complementary to Aspire rather than competing with it: Aspire orchestrates *where things run and how they're observed*, Dapr provides *portable building blocks* a service calls into via its sidecar.
- **Steeltoe** - an older, Spring Cloud-inspired toolkit (externalized configuration, service discovery, circuit breakers), predating Aspire by several years. Still maintained, but Aspire has absorbed most of the reasons a new project would have reached for it.
- **YARP** - Microsoft's reverse proxy toolkit. Not an orchestration or discovery answer by itself; it's the gateway layer regardless of which of the above a project chooses underneath.

**This tutorial uses .NET Aspire** for local orchestration, service discovery, and observability - the current, first-party default for a new .NET distributed application in 2026, and the choice that needs the least justification of the four:

- An `AppHost` project (`src/AppHost/`) declares every service and every infrastructure resource (PostgreSQL, Kafka, Redis) in plain C#, with dependencies expressed as `.WithReference(...)` calls - the AppHost *is* the architecture diagram, executable
- Service discovery falls out of that graph for free: `Microsoft.Extensions.ServiceDiscovery`, already Microsoft's own official package, resolves a logical address like `https+http://catalog-api` using configuration Aspire injects automatically wherever `.WithReference(catalogService)` was declared - no registry process to run, no custom provider to write
- A shared `ServiceDefaults` project (`src/ServiceDefaults/`), the standard Aspire template convention, wires OpenTelemetry, health check endpoints, and default HTTP resilience identically into every service with one `builder.AddServiceDefaults()` call
- The Aspire Dashboard receives every service's OpenTelemetry traces, metrics, and logs over OTLP automatically (via `ServiceDefaults`) and correlates them into one connected view per request - no separate tracing backend to stand up

The honest caveat, stated plainly rather than glossed over: Aspire's `AppHost` is primarily a *local development and testing* orchestrator. Its production story (via `aspire publish`, which can emit a Docker Compose file, a Kubernetes manifest, or Azure Container Apps' Bicep, depending on the target) is real but still younger than a battle-tested standalone registry like Consul or Kubernetes' own service discovery - a team deploying to a mature Kubernetes platform would likely lean on that platform's own discovery mechanism for production and use Aspire purely for the local dev loop. For this tutorial's purposes - and for the great majority of .NET teams starting a new distributed system today - Aspire's local-first, first-party, zero-registry-to-operate model is the right default, and it's what the rest of this document builds on.

## Service decomposition

Five business services, plus one entry point with no database of its own:

| Service | Owns |
|---|---|
| `identity-api` | Users, roles, permissions, refresh tokens, a blacklist of revoked access tokens, an audit log of every role/permission change |
| `catalog-api` | Categories, products |
| `customer-api` | Customer profiles |
| `order-api` | Orders, idempotency keys |
| `notification-worker` | Nothing persisted - reacts to events, sends notifications |

| Service | Role |
|---|---|
| `api-gateway` | YARP reverse proxy - single entry point, JWT validation, rate limiting, routing to every business service |

## Why `Users` (identity) and `Customers` (e-commerce) stay in separate services

A user account and a customer profile both hold a name and an email, which makes it tempting to merge them into one table. They're kept **deliberately separate services**, because they answer different questions:

- `identity-api`'s users = *can this request authenticate, and what is it allowed to do* - identity, credentials, roles, permissions
- `customer-api`'s customers = *who is this business-wise* - billing address, phone, order history context

`customer-api` never queries `identity-api`'s database directly (no cross-service foreign key). It stores a `UserId` column referencing the identity created in `identity-api`, resolved only through that service's API when needed. This is the standard **database-per-service** boundary: a foreign key that crosses a service boundary is a sign the boundary is wrong, so IDs are carried as plain values, not enforced constraints.

## Scope: what this tutorial covers, and what it deliberately leaves out

**Covered**, because each one exposes a genuine microservices concern that a single-service project can't: local orchestration and service discovery (Aspire), centralized cross-cutting service configuration (`ServiceDefaults`), an API gateway, synchronous inter-service calls (Refit), asynchronous event-driven communication (Kafka), a choreographed Saga with a compensating action, idempotent writes, distributed tracing, circuit breakers, rate limiting, health-check groups, and consumer-driven contract testing.

**Deliberately left out**, and why:
- **Service mesh** (Istio, Linkerd) - an alternative to Aspire/YARP/`Microsoft.Extensions.ServiceDiscovery` for cross-cutting network concerns at the infrastructure level, not a complement; mixing both would teach two competing ways to solve the same problems
- **Kubernetes** - a legitimate follow-up, and one Aspire can target via `aspire publish`, but large enough (manifests, Ingress, ConfigMaps, readiness/liveness wiring, Helm) to deserve its own tutorial
- **Secrets management** (Vault, Azure Key Vault) - genuinely important in production, but orthogonal to the microservices patterns this tutorial focuses on; local development uses .NET user-secrets/environment variables, which hold no real secrets here
- **A custom outbox implementation** - MassTransit's built-in EF Core outbox (`AddEntityFrameworkOutbox`) is used instead of hand-rolling one; see [The Saga in detail](#the-saga-in-detail-order-confirmation)

## Tech stack

| Component | Choice | Why |
|---|---|---|
| Framework | ASP.NET Core 10 (.NET 10 LTS) | Current LTS |
| Language | C# 13 | |
| Orchestration & local dev | .NET Aspire 13.x (`AppHost` project) | The current first-party default for a new .NET distributed application - declares every service and resource in C#, drives local orchestration end to end |
| Service discovery | `Microsoft.Extensions.ServiceDiscovery`, wired automatically by Aspire's `AppHost` resource references | No custom provider to write - Aspire injects the configuration this official package already knows how to resolve |
| Cross-cutting service defaults | A shared `ServiceDefaults` project (the standard Aspire template convention) | One `builder.AddServiceDefaults()` call wires OpenTelemetry, health checks, and HTTP resilience identically into every service |
| API Gateway | YARP (`Yarp.ReverseProxy`) | A programmable reverse proxy built and maintained by Microsoft, configuration-driven |
| Synchronous inter-service calls | Refit | An interface annotated with HTTP attributes, generating a typed `HttpClient` at build time |
| Asynchronous inter-service calls | Apache Kafka (KRaft mode), via MassTransit's Kafka rider | MassTransit wraps `Confluent.Kafka` with publish/consume abstractions, retry policies, and OpenTelemetry integration, instead of a hand-rolled producer/consumer wrapper - its built-in EF Core outbox (`AddEntityFrameworkOutbox`) is also what makes event publication atomic with the database write that triggers it |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly) | The current built-in way to attach a circuit breaker/retry/timeout pipeline to a typed `HttpClient` |
| Rate limiting | ASP.NET Core's built-in `RateLimiter` middleware, Redis-backed distributed store | Built into the framework since .NET 7, no extra dependency for the algorithm itself |
| Distributed tracing | OpenTelemetry .NET, viewed in the Aspire Dashboard | `ServiceDefaults` already exports every service's traces/metrics/logs over OTLP - the dashboard that ships with Aspire receives them with no separate backend to run |
| Contract testing *(bonus)* | PactNet | The .NET implementation of the Pact consumer-driven-contract standard |
| Build | One solution (`DotnetMicroservicesTutorial.sln`) at the repo root referencing every service project - a convenience for building/testing together, not a source of shared runtime code by itself; see [Common.Lib](#commonlib-what-belongs-in-a-shared-project-and-what-never-does) | |
| Database | PostgreSQL 16, **one database per service**, each declared as an Aspire resource (`AddPostgres().AddDatabase(...)`), four separate databases | |
| ORM | Entity Framework Core 10 | |
| Migrations | EF Core Migrations, one migration history per service | |
| DTO mapping | Mapster | |
| Validation | FluentValidation | |
| Security | ASP.NET Core Identity + JWT (`identity-api`), validated at the gateway and again at each service | |
| API documentation | Swashbuckle (Swagger UI) per service | |
| Monitoring | ASP.NET Core Health Checks on every service, with `live`/`ready` tag-based groups | |
| Tests | xUnit, Moq, Testcontainers for .NET, `WebApplicationFactory`, WireMock.Net for stubbing Refit dependencies in isolation, `Aspire.Hosting.Testing` for cross-service integration tests | |
| CI/CD | GitHub Actions, one workflow per service, only building/testing what changed | |
| Containerization | Docker, for every containerized resource Aspire itself manages (PostgreSQL, Kafka, Redis); a deployable `docker-compose.yml` is *generated* via `aspire publish --publisher docker-compose` rather than hand-maintained | |

## Architecture overview

```
                              ┌──────────────────┐
                              │  AppHost (Aspire) │  (declares every service + resource in C#,
                              └─────────▲──────────┘   drives local orchestration + service discovery)
                                        │ orchestrates / injects config
                  ┌─────────────────────┼─────────────────────┬───────────────────┐
                  │                     │                     │                   │
         ┌────────┴───────┐   ┌─────────┴────────┐   ┌────────┴────────┐  ┌───────┴────────┐
         │  identity-api   │   │ catalog-api  │   │ customer-api │  │notification-svc│
         └────────▲────────┘   └─────────▲────────┘   └────────▲────────┘  └───────▲────────┘
                  │                     │                     │                   │
                  │            ┌────────┴────────┐            │            Kafka topic
                  │            │  order-api   │────────────┘        "order-events"
                  │            └────────▲─────────┘                          ▲
                  │                     │  (Refit, sync)                     │
                  │                     └──────────────────────publishes─────┘
                  │                       (OrderCreatedEvent, async, MassTransit/Kafka)
                  │
                  └─────────────────────┬─────────────────────┐
                                        │                     │
                              ┌─────────┴────────┐            │
                              │   api-gateway     │            │
                              │  (YARP; rate      │            │
                              │  limiting, JWT     │◄───────────┘ (discovered via Aspire too)
                              │  validation)       │
                              └─────────▲─────────┘
                                        │
                                    client apps

Cross-cutting, present on every service via ServiceDefaults: OpenTelemetry → Aspire Dashboard,
ASP.NET Core Health Checks (live/ready), Common.Lib (base exceptions, ProblemDetails mapping).
```

- Every business service's identity in the system comes from being declared once in `AppHost`, which resolves service discovery and injects connection configuration for every resource it references at startup
- `order-api` calls `catalog-api`/`customer-api` **synchronously** (Refit) to validate a product/customer exist before creating an order
- `order-api` also publishes an `OrderCreatedEvent` **asynchronously** (Kafka, via MassTransit) after committing the order; `notification-worker` consumes it - this is the tutorial's second, deliberately different communication style, and the trigger for the choreographed Saga (see [Design patterns used](#design-patterns-used))
- `api-gateway` is the only service exposed to the outside world; every other service is reachable only inside the Docker network

## Load balancing and service discovery

Every inter-service call in this tutorial is **client-side load balanced**, resolved through Aspire's service discovery rather than a fixed host:

- In `AppHost`, `order-api`'s declaration includes `.WithReference(catalogService).WithReference(customerService)` - this is what tells Aspire (and, through it, `Microsoft.Extensions.ServiceDiscovery`) that `order-api` is allowed to resolve those two logical names, and Aspire injects the connection information as configuration at startup
- Refit clients are registered as typed `HttpClient`s with a base address of `https+http://catalog-api` (a logical name, not a host:port); `.AddServiceDiscovery()` on the `HttpClientBuilder` resolves it using the configuration Aspire already injected - if a service is scaled to multiple instances (in a `publish` target that supports it), calls are distributed across all of them without any code change in the caller
- `api-gateway`'s YARP routes point at the same logical service names, resolved through the same `Microsoft.Extensions.ServiceDiscovery` mechanism - no separate refresh loop or polling service is needed locally, since Aspire's `AppHost` already knows the full topology and injects it once at startup
- There is no separate load-balancer *service* to deploy, and no registry process to operate - it's a client-side concern (the Refit `HttpClient`, the gateway's resolver), backed entirely by configuration Aspire wires in

## Data model

Four separate databases, one per business service with its own schema; `notification-worker` has none. Only relationships *within* a database are real foreign keys - every relationship that would cross a service boundary is deliberately a plain column instead.

```
identity-api DB (identity_db):
    AspNetUsers (Id, UserName, Email, PasswordHash, LockoutEnabled, LockoutEnd, ...)
        │ N──N (AspNetUserRoles)
    AspNetRoles (Id, Name)
        │ N──N (role_permissions)
    permissions (id, resource, action)
    refresh_tokens (id, user_id, token_hash, family_id, security_stamp_at_issuance,
                     created_at, expires_at, revoked_at, replaced_by_token_id)
    blacklisted_access_tokens (id, user_id, jti, blacklisted_at, expires_at)
    audit_logs (id, actor_user_id, action, entity_type, entity_id, details, created_at)
    OutboxMessage, OutboxState (MassTransit's own tables, added by AddEntityFrameworkOutbox -
        this is where UserRegisteredEvent/PasswordResetRequestedEvent actually land, in the
        same transaction as the row that triggered them, before MassTransit relays them to Kafka)
    Every relationship above is a real foreign key - the whole schema lives in one database.

customer-api DB (customer_db):
    customers (Id, UserId, FirstName, LastName, Telephone, Email, Address)
        └─ UserId: plain value referencing identity-api's AspNetUsers.Id, no FK

catalog-api DB (catalog_db):
    categories (Id, CategoryName)
        │ 1
        │
        │ N
    products (Id, CategoryId, ProductName, UnitPrice)
        A real foreign key - both tables live in catalog_db

order-api DB (order_db):
    orders (Id, CustomerId, ProductId, Quantity, Total, Status)
        └─ CustomerId: plain value referencing customer-api's customers.Id, no FK
        └─ ProductId: plain value referencing catalog-api's products.Id, no FK
        └─ Status: Pending, Confirmed, ConfirmationFailed
    idempotency_keys (Id, IdempotencyKey, OrderId, CreatedAt)
        └─ OrderId: FK → orders.Id (same database, real FK)
        └─ IdempotencyKey: UNIQUE, supplied by the client as an
           "Idempotency-Key" header on POST /api/v1/Orders
    OutboxMessage, OutboxState (MassTransit's own tables - same role as identity_db's above,
        this is where OrderCreatedEvent actually lands atomically with the order it describes)

notification-worker:
    No database - stateless Kafka consumer
```

## Common.Lib: what belongs in a shared project, and what never does

Every business service duplicating exception types, `ProblemDetails` mapping, and trace-aware logging independently is the obvious first instinct - and it doesn't hold up once those concerns are actually thought through. In a **monorepo**, the usual risk of a shared project (services silently drifting onto different versions) is largely neutralized: `Common.Lib` and every service are built and tested together, on the same commit, in the same CI - a breaking change in `Common.Lib` fails the whole build immediately, not six months later in one forgotten service.

So `Common.Lib` exists, as a plain project reference (never a deployable service), with a strict rule about its contents - **distinct from `ServiceDefaults`**, which every service also references: `ServiceDefaults` is Aspire's own convention for wiring OpenTelemetry, health checks, and HTTP resilience (infrastructure plumbing, generated by the Aspire project templates), while `Common.Lib` is this project's own convention for exceptions, `ProblemDetails` mapping, and response shaping (application-level cross-cutting code). Neither ever depends on the other.

**Allowed in `Common.Lib`** - cross-cutting infrastructure that should almost never change and carries no business meaning on its own:
- `ResourceNotFoundException`, `BusinessRuleException`
- `GlobalExceptionHandler` (`IExceptionHandler`), mapping those exceptions to `ProblemDetails` - each service registers it via `AddExceptionHandler<GlobalExceptionHandler>()`
- `PageResponse<T>` metadata helper for building `X-Total-Count`/`Link` pagination headers

**Never allowed in `Common.Lib`**, even if it would be convenient:
- Any EF Core entity, `DbContext`, or domain-specific DTO (`ProductResponse` stays in `catalog-api`, always)
- Any business rule
- Any dependency on a specific service's data shape

## Design patterns used

Every architectural choice above is a named pattern from the microservices literature (mostly Chris Richardson's [microservices.io](https://microservices.io) catalog). Naming them explicitly is deliberate: knowing the pattern name is what lets the concept transfer to a different stack later, rather than staying tied to this specific tutorial's tooling.

| Pattern | Category | Problem it solves | Where in this project |
|---|---|---|---|
| Database per Service | Data | Keep each service's schema private so services can evolve and scale independently | Every business service, one schema each (see [Data model](#data-model)) |
| Shared Kernel, deliberately narrowed | Data / code sharing | Cross-cutting boilerplate duplicated identically in every service | `Common.Lib`, with a strict allow-list (see above); normally an anti-pattern across independently-deployed services, viable here specifically because it's a monorepo built as one unit |
| Service Registry, client-side discovery | Discovery | Callers need up-to-date instance locations without hardcoding hosts/ports | Aspire's `AppHost` graph + `Microsoft.Extensions.ServiceDiscovery`, used by both Refit and the gateway (see [Load balancing](#load-balancing-and-service-discovery)) |
| Externalized Configuration | Configuration | Change a service's config without rebuilding or redeploying it | ASP.NET Core's layered `IConfiguration` (`appsettings.{Environment}.json`, environment variables, user-secrets), with connection/service endpoints injected by Aspire on top |
| API Gateway | Communication | One entry point for routing, authentication, and rate limiting instead of every client talking to every service directly | `api-gateway` (YARP) |
| API Composition | Query | Assemble one response from data that lives in several services | `GET /api/v1/Orders/{id}`, enriched with product/customer data resolved via Refit |
| Circuit Breaker | Resilience | Stop calling a downstream that's already failing, fail fast instead of piling up timeouts | `feature/resilience`, `Microsoft.Extensions.Http.Resilience` around the `ProductClient`/`CustomerClient` Refit clients |
| Rate Limiter | Resilience | Protect a service (here, `identity-api`) from being overwhelmed by a single client | ASP.NET Core `RateLimiter` middleware, Redis-backed, on `/api/v1/Auth/Login` at the gateway |
| Saga, choreography-based | Data consistency | Coordinate a business transaction across services without a distributed transaction/2PC | `order-api` ↔ `notification-worker`, full walkthrough below |
| Idempotent Consumer | Messaging / resilience | Safely handle a client retry or a duplicate request without creating a duplicate resource | `Idempotency-Key` header + `idempotency_keys` table on `POST /api/v1/Orders` |
| Transactional Outbox | Data consistency / messaging | Make "persist the business change" and "publish the resulting event" one atomic operation, so a crash between the two can never silently drop an event | MassTransit's built-in EF Core outbox (`AddEntityFrameworkOutbox`) on `order-api` and `identity-api`, the two publishers in this system |
| Event-Driven, Publish-Subscribe | Communication | Decouple the producer from its consumers, let new consumers subscribe later without the producer changing | Kafka topics: `order-events`, `notification-events`, `identity-events` |
| Health Check API | Observability | Let an orchestrator or load balancer know whether an instance is alive and ready for traffic, separately | ASP.NET Core Health Checks, `live`/`ready` tag-based groups on every service |
| Distributed Tracing | Observability | Follow one logical request as it crosses multiple service boundaries, both sync and async | OpenTelemetry .NET, viewed in the Aspire Dashboard (`feature/observability`) |
| Consumer-Driven Contract Testing | Testing | Catch a producer's breaking API change at build time, before it ever reaches a deployed consumer | `feature/contract-testing` (bonus), PactNet |

### The Saga in detail: order confirmation

The only two-way choreography in this tutorial. No orchestrator, no distributed transaction: `order-api` and `notification-worker` each manage only their own local transaction, coordinated entirely by reacting to each other's events.

```
1. client          -- POST /api/v1/Orders (Idempotency-Key: <key>) -->  order-api
2. order-api    checks idempotency_keys for <key>; not found, continues
3. order-api    validates ProductId/CustomerId synchronously (Refit: ProductClient, CustomerClient)
4. order-api    persists Order(Status=Pending) + IdempotencyKey, same local transaction, commits
5. order-api    -- publishes OrderCreatedEvent{OrderId, ...} on "order-events" -->  (only after step 4 commits)
6. notification-worker  OrderCreatedEventConsumer receives it, calls EmailNotification.Worker

   nominal path                                    simulated-failure path
   ─────────────                                    ──────────────────────
7a. "e-mail" logged successfully                  7b. "e-mail" fails (configured trigger)
8a. OrderConfirmedEvent published                 8b. NotificationFailedEvent published
    on "notification-events"                          on "notification-events"
9a. order-api's OrderConfirmedEventConsumer   9b. order-api's NotificationFailedEventConsumer
    sets Order.Status: Pending → Confirmed             sets Order.Status: Pending → ConfirmationFailed
```

An order is never stuck in `Pending` forever by design: exactly one of the two outcome events always follows `OrderCreatedEvent`, so `order-api` always eventually hears back.

**Event topics at a glance**

| Topic | Producer | Consumer | Events carried | Part of the Saga? |
|---|---|---|---|---|
| `order-events` | `order-api` | `notification-worker` | `OrderCreatedEvent` | Triggers it |
| `notification-events` | `notification-worker` | `order-api` | `OrderConfirmedEvent`, `NotificationFailedEvent` | Both outcomes |
| `identity-events` | `identity-api` | `notification-worker` | `UserRegisteredEvent`, `PasswordResetRequestedEvent` | No, fire-and-forget |

**The Transactional Outbox, actually implemented**: publishing right after the local transaction commits still leaves a real gap - the commit and the publish are two separate operations, and a crash between them would silently drop an event. Microsoft's own `dotnet/eShop` reference application doesn't accept that gap (it ships its own hand-rolled `IntegrationEventLogEF` to close it), and neither does this project - via MassTransit's built-in EF Core outbox (`AddEntityFrameworkOutbox<AppDbContext>()`), rather than a custom implementation:

- Both `order-api` and `identity-api` (the two publishers in this system) configure `AddEntityFrameworkOutbox<AppDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); })` in their `Program.cs`
- MassTransit adds its own `OutboxMessage`/`OutboxState` tables to each publisher's schema, created by the same EF Core migration as the rest of that service's tables
- `IPublishEndpoint.Publish(...)` (the same call `OrderService`/`UserService` already make) is automatically intercepted: instead of touching Kafka directly, it writes the outgoing message into `OutboxMessage`, inside whatever `DbContext.SaveChangesAsync()` call is already in flight - the business write and the outbox write become one atomic transaction, no code change needed at the call site
- A `IHostedService` MassTransit adds automatically delivers outbox rows to Kafka after the transaction commits, retrying on failure, deleting the row only once delivery is confirmed - if the process crashes before delivery, the row is still there on restart and gets delivered then

This closes the exact gap the previous section's own diagram glosses over at step 5 (`persists Order(Status=Pending) + IdempotencyKey, same local transaction, commits` → `publishes OrderCreatedEvent`) - those two steps are now genuinely one atomic unit, not "committed, then hopefully published."

## Branching strategy

| Branch | Role |
|---|---|
| `master` | Stable, production-ready code. No direct commits, only merges from `develop`. |
| `develop` | Integration branch. |
| `feature/common-lib` | Shared project: base exceptions, `GlobalExceptionHandler`, `PageResponse<T>`. |
| `feature/infrastructure` | `AppHost` project (Aspire orchestration, resource declarations), `ServiceDefaults` project (OpenTelemetry, health checks, resilience defaults). |
| `feature/identity-api` | Identity/RBAC service, packaged as a microservice. |
| `feature/catalog-api` | Categories/products service. |
| `feature/customer-api` | Customer profile service. |
| `feature/order-api` | Orders service: Refit calls, idempotent creation, publishes `OrderCreatedEvent`. |
| `feature/notification-worker` | Kafka consumer, choreographed Saga compensating action. |
| `feature/api-gateway` | YARP gateway: single entry point, JWT validation, rate limiting, Aspire-driven service discovery routing. |
| `feature/observability` | OpenTelemetry .NET across every service, viewed in the Aspire Dashboard. |
| `feature/resilience` | `Microsoft.Extensions.Http.Resilience` circuit breakers and fallbacks on `order-api`'s Refit clients. |
| `feature/contract-testing` | *Bonus*: PactNet between `order-api` and its two Refit dependencies. |

## Repository structure

```
dotnet-microservices-tutorial/
├── DotnetMicroservicesTutorial.sln          (references every project below, convenience only - see Common.Lib)
├── .github/
│   ├── PULL_REQUEST_TEMPLATE.md
│   └── workflows/
│       ├── ci-common-lib.yml
│       ├── ci-identity-api.yml
│       ├── ci-catalog-api.yml
│       ├── ci-customer-api.yml
│       ├── ci-order-api.yml
│       ├── ci-notification-worker.yml
│       └── ci-api-gateway.yml
├── src/
│   ├── AppHost/                                (Aspire orchestrator - not a deployable service)
│   │   ├── AppHost.cs                           (declares every resource and every service, e.g.:
│   │   │                                          var postgres = builder.AddPostgres("postgres");
│   │   │                                          var identityDb = postgres.AddDatabase("identity-db");
│   │   │                                          var catalogDb = postgres.AddDatabase("catalog-db");
│   │   │                                          var customerDb = postgres.AddDatabase("customer-db");
│   │   │                                          var orderDb = postgres.AddDatabase("order-db");
│   │   │                                          var kafka = builder.AddKafka("kafka");
│   │   │                                          var redis = builder.AddRedis("redis");
│   │   │                                          var identityService = builder.AddProject<Projects.Identity_API>("identity-api")
│   │   │                                              .WithReference(identityDb).WithReference(kafka);
│   │   │                                          var catalogService = builder.AddProject<Projects.Catalog_API>("catalog-api")
│   │   │                                              .WithReference(catalogDb);
│   │   │                                          var customerService = builder.AddProject<Projects.Customer_API>("customer-api")
│   │   │                                              .WithReference(customerDb);
│   │   │                                          var orderService = builder.AddProject<Projects.Order_API>("order-api")
│   │   │                                              .WithReference(orderDb).WithReference(kafka)
│   │   │                                              .WithReference(catalogService).WithReference(customerService);
│   │   │                                          var notificationService = builder.AddProject<Projects.Notification_Worker>("notification-worker")
│   │   │                                              .WithReference(kafka);
│   │   │                                          builder.AddProject<Projects.ApiGateway>("api-gateway")
│   │   │                                              .WithReference(redis).WithReference(identityService)
│   │   │                                              .WithReference(catalogService).WithReference(customerService)
│   │   │                                              .WithReference(orderService).WithReference(notificationService)
│   │   │                                              .WithExternalHttpEndpoints();)
│   │   └── appsettings.json
│   ├── ServiceDefaults/                        (Aspire's standard shared convention - referenced by every service)
│   │   └── Extensions.cs                        (AddServiceDefaults(): OpenTelemetry, health check endpoints,
│   │                                              default HTTP resilience - identical in every service)
│   ├── Common.Lib/
│   │   ├── Exceptions/
│   │   │   ├── ResourceNotFoundException.cs
│   │   │   ├── BusinessRuleException.cs
│   │   │   └── GlobalExceptionHandler.cs     (IExceptionHandler, maps to ProblemDetails)
│   │   └── Dtos/
│   │       └── PageResponse.cs
│   ├── Contracts/                             (MassTransit message contracts only - referenced by every
│   │   │                                        producer/consumer pair below, never by anything else)
│   │   ├── OrderCreatedEvent.cs
│   │   ├── OrderConfirmedEvent.cs
│   │   ├── NotificationFailedEvent.cs
│   │   ├── UserRegisteredEvent.cs
│   │   └── PasswordResetRequestedEvent.cs
│   ├── Identity.API/
│   │   ├── Program.cs                          (first line: builder.AddServiceDefaults())
│   │   ├── Models/ (AppUser, AppRole, Permission, RolePermission, RefreshToken, BlacklistedAccessToken, AuditLog)
│   │   ├── Data/ (AppDbContext, Configurations/)
│   │   ├── Consumers/                          (empty for now - Identity.API only publishes)
│   │   ├── Services/ (IUserService.cs + UserService.cs, IRoleService.cs + RoleService.cs, side by side)
│   │   ├── Controllers/
│   │   └── Security/ (JwtService, token issuance/rotation)
│   ├── Catalog.API/
│   │   ├── Program.cs                          (first line: builder.AddServiceDefaults())
│   │   ├── Models/ (Category, Product)
│   │   ├── Data/
│   │   ├── Dtos/
│   │   ├── Services/ (ICategoryService.cs + CategoryService.cs, IProductService.cs + ProductService.cs)
│   │   ├── Controllers/
│   │   └── PactTests/                          (bonus: PactNet producer verification)
│   ├── Customer.API/
│   │   ├── Program.cs                          (first line: builder.AddServiceDefaults())
│   │   ├── Models/ (Customer, with a plain UserId column)
│   │   ├── Data/
│   │   ├── Dtos/
│   │   ├── Services/ (ICustomerService.cs + CustomerService.cs)
│   │   ├── Controllers/
│   │   └── PactTests/                          (bonus: PactNet producer verification)
│   ├── Order.API/
│   │   ├── Program.cs                          (first line: builder.AddServiceDefaults())
│   │   ├── Models/ (Order, OrderStatus, IdempotencyKey)
│   │   ├── Data/
│   │   ├── Clients/
│   │   │   ├── IProductClient.cs               (Refit interface → catalog-api, base address https+http://catalog-api)
│   │   │   ├── ICustomerClient.cs               (Refit interface → customer-api)
│   │   │   └── Fallbacks/                        (resilience fallback handlers)
│   │   ├── Consumers/
│   │   │   ├── OrderConfirmedEventConsumer.cs    (consumes the Saga's success event, from Contracts)
│   │   │   └── NotificationFailedEventConsumer.cs (consumes the Saga's compensating event, from Contracts)
│   │   ├── Dtos/
│   │   ├── Services/ (IOrderService.cs + OrderService.cs)
│   │   └── Controllers/
│   ├── Notification.Worker/
│   │   ├── Program.cs                          (first line: builder.AddServiceDefaults())
│   │   ├── Consumers/
│   │   │   ├── OrderCreatedEventConsumer.cs
│   │   │   ├── UserRegisteredEventConsumer.cs        (sends the activation email)
│   │   │   └── PasswordResetRequestedEventConsumer.cs (sends the password-reset email)
│   │   └── Services/ (IEmailNotification.Worker.cs + EmailNotification.Worker.cs - logs instead of
│   │                    sending real email in this tutorial)
│   └── ApiGateway/
│       ├── Program.cs                          (first line: builder.AddServiceDefaults())
│       ├── Security/
│       │   └── JwtValidationMiddleware.cs      (verifies signature/expiration of JWTs issued by identity-api)
│       └── Config/
│           ├── YarpServiceDiscoveryConfig.cs    (YARP cluster destinations expressed as Aspire-resolved
│           │                                      logical addresses, e.g. https+http://catalog-api)
│           └── RateLimiterPolicies.cs           (Redis-backed policy applied to /api/v1/Auth/Login)
└── tests/
    ├── Common.Lib.Tests/
    ├── Identity.API.Tests/
    ├── Catalog.API.Tests/
    ├── Customer.API.Tests/
    ├── Order.API.Tests/                     (includes WireMock.Net stubs for IProductClient/ICustomerClient)
    ├── Notification.Worker.Tests/               (embedded/Testcontainers Kafka)
    └── ApiGateway.Tests/
```

## Standard response format

Every service follows ASP.NET Core's own idiom rather than a custom envelope:

- **Success**: an endpoint returns the resource itself - `ActionResult<T>`/`TypedResults` with the DTO directly as the body, `201 Created` via `CreatedAtAction`, `204 No Content` for actions with nothing to return. Paginated list endpoints return the collection directly, with `X-Total-Count`/`Link` pagination headers (`Common.Lib`'s `PageResponse<T>` builds them) rather than a wrapper object.
- **Failure**: every non-2xx response is a `ProblemDetails` (RFC 9457), produced by `Common.Lib`'s `GlobalExceptionHandler` + `AddProblemDetails()`.
- `api-gateway` passes each service's response straight through unmodified - it never rewraps or reshapes response bodies, only routes, authenticates, and rate-limits.

## Testing strategy

Every service ships its tests before its Pull Request is opened, at the layers that apply to what the service does. Each service is tested in isolation from its dependencies, and CI builds and tests each service independently.

| Layer | Tool | What it verifies |
|---|---|---|
| Repository/Data | EF Core + Testcontainers (real PostgreSQL) | Queries, constraints and mappings against a real schema |
| Service | xUnit + Moq | Business rules and orchestration, with every repository and client dependency mocked |
| Controller | `WebApplicationFactory` + `HttpClient` | HTTP status codes, payload shape, and `ProblemDetails` mapping, with the service layer mocked or in-memory |
| Client | WireMock.Net | Refit clients against stubbed downstream services, including failures and fallbacks |
| Messaging | Testcontainers Kafka | Event publication after commit, consumption and choreography |
| Gateway | xUnit + `WebApplicationFactory` | Routing, JWT validation and rate limiting |
| Cross-service integration *(optional, heavier)* | `Aspire.Hosting.Testing` (`DistributedApplicationTestingBuilder`) | Spins up a real subset of the distributed app (e.g. `order-api` + `catalog-api` + `customer-api` together) for a genuine end-to-end test, without WireMock.Net stubs standing in for a real dependency |

### Test naming convention

Every test method, at every layer and in every project, follows `MethodName_ShouldExpectedOutcome_WhenCondition`:

```csharp
[Fact]
public async Task GetById_ShouldReturnProduct_WhenProductExists() { ... }

[Fact]
public async Task GetById_ShouldReturnNotFound_WhenProductDoesNotExist() { ... }
```

No other naming style is used anywhere in this project's test suite.

## feature/common-lib

First branch, since every service below depends on it.

### Tasks

- [x] `ResourceNotFoundException`, `BusinessRuleException`, `GlobalExceptionHandler` (`IExceptionHandler`, mapping to `ProblemDetails`/`ValidationProblemDetails`)
- [x] `PageResponse<T>` header-building helper
- [x] Referenced as a regular project reference (`<ProjectReference>`) by every service, never copy-pasted
- [x] Unit tests for `GlobalExceptionHandler`'s mapping of each exception type
- [x] `.github/workflows/ci-common-lib.yml`, and every other service's CI job depends on this one succeeding first (since they all compile against it)
- [x] `.github/PULL_REQUEST_TEMPLATE.md`: repo-wide, used by every `feature/*` branch's PR from here on

## feature/infrastructure

The Aspire orchestration layer and the shared cross-cutting service wiring. No business logic. Merged after `common-lib`.

### Tasks

- [x] `src/AppHost/`: created via the standard Aspire project templates (`dotnet new aspire-apphost`), `AppHost.cs` starts with just the resource declarations that exist so far (PostgreSQL server, one database per business service, a Kafka resource) - every business service is added to it incrementally, in its own branch, as that service is built
- [x] `src/ServiceDefaults/`: created via `dotnet new aspire-servicedefaults`, exposing one `AddServiceDefaults()` extension method that wires OpenTelemetry (traces, metrics, logs, OTLP exporter pointed at the Aspire dashboard), ASP.NET Core Health Checks (`/health/live`, `/health/ready`), and default HTTP resilience for outgoing `HttpClient`s - referenced by every service from this branch onward
- [x] Every service's `Program.cs` calls `builder.AddServiceDefaults()` as its first line, before anything else is registered
- [x] ASP.NET Core Health Checks convention established: every service exposes `/health/live` (process is up) and `/health/ready` (dependencies - DB, Kafka - are reachable) via tagged health check groups, provided by `ServiceDefaults`
- [x] `.github/workflows/ci-infrastructure.yml` (covering `AppHost`/`ServiceDefaults` build) or folded into `ci-common-lib.yml`

## feature/identity-api

Identity and access control, packaged as its own microservice - with one deliberate design choice: it never sends an e-mail itself.

### Endpoints (via the gateway: `/api/v1/Auth/**`)

| Method | URL | Description |
|---|---|---|
| POST | `/api/v1/Auth/Register` | Register (creates a disabled user + confirmation token, publishes `UserRegisteredEvent`) |
| GET | `/api/v1/Auth/ConfirmEmail` | Confirms the account |
| POST | `/api/v1/Auth/Login` | Returns an access + refresh token pair - rate-limited at the gateway |
| POST | `/api/v1/Auth/Refresh` | Exchanges a valid refresh token for a new pair |
| POST | `/api/v1/Auth/Logout` | Blacklists the current access token, revokes the refresh token family |
| GET | `/api/v1/Auth/Me` | Current user profile, roles, and permissions |
| POST | `/api/v1/Auth/ForgotPassword` | Generates a password-reset token, publishes `PasswordResetRequestedEvent` |
| POST | `/api/v1/Auth/ResetPassword` | Consumes the reset token, updates the password |

### Tasks

- [x] `src/Contracts/` project created (if not already, from `feature/common-lib`'s dependency graph): `UserRegisteredEvent`, `PasswordResetRequestedEvent` message contracts, referenced as a project reference by both `identity-api` (publisher) and `notification-worker` (consumer, added in a later branch) - never duplicated as two independently-defined classes that happen to look alike
- [x] ASP.NET Core Identity (`AppUser : IdentityUser<int>`, `AppRole : IdentityRole<int>`), custom `Permission`/`RolePermission` entities for resource/action authorization beyond Identity's own role-only model
- [x] `RefreshToken`, `BlacklistedAccessToken`, `AuditLog` entities
- [x] **No `IEmailService` in this service.** It publishes an event and lets `notification-worker` handle delivery - the same "one service owns everything that leaves the system" rule applied to `order-api`
- [x] `AddEntityFrameworkOutbox<AppDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); })` configured on the MassTransit registration - see [The Saga in detail](#the-saga-in-detail-order-confirmation)
- [x] `identity-api` publishes `UserRegisteredEvent` (UserId, Email, confirmation token) right after registration, and `PasswordResetRequestedEvent` (UserId, Email, reset token) right after a reset is requested - both calls are ordinary `IPublishEndpoint.Publish(...)`, and the outbox above is what makes each one land in the same transaction as the row that triggered it, atomically, rather than relying on careful call ordering
- [x] A custom claims principal factory embeds the user's resolved permissions into the JWT at login, so downstream permission checks never need a database call
- [x] Depends on `Common.Lib`
- [x] `JwtService` signs tokens with a shared secret/key, documented clearly since every other business service and `api-gateway` need to validate the same tokens independently
- [x] Added to `src/AppHost/AppHost.cs`: `builder.AddProject<Projects.Identity_API>("identity-api").WithReference(identityDb).WithReference(kafka)`
- [x] Unit, repository, and controller tests, plus a test verifying both events end up in `OutboxMessage` within the same transaction as the row that triggered them, and are relayed to Kafka only after that transaction commits

## feature/catalog-api

### Endpoints (`/api/v1/Catalog/**`)

| Method | URL | Description |
|---|---|---|
| GET | `/api/v1/Catalog/Categories` | Paginated list |
| POST | `/api/v1/Catalog/Categories` | Create (Admin) |
| GET | `/api/v1/Catalog/Products` | Paginated list, filterable by `categoryId` |
| GET | `/api/v1/Catalog/Products/{id}` | Detail - called by `order-api` via Refit |
| POST | `/api/v1/Catalog/Products` | Create (Admin) |

### Tasks

- [x] `Category`, `Product` models, `AppDbContext`, DTOs, Mapster mapping, FluentValidation validators, interface-backed services (side by side in `Services/`, no separate folder split), controllers
- [x] Depends on `Common.Lib`
- [x] Added to `src/AppHost/AppHost.cs`: `builder.AddProject<Projects.Catalog_API>("catalog-api").WithReference(catalogDb)`
- [x] Tests, including one verifying `GET /api/v1/Catalog/Products/{id}`'s exact response shape (formalized later by `feature/contract-testing`)

## feature/customer-api

### Endpoints (`/api/v1/Customers/**`)

| Method | URL | Description |
|---|---|---|
| GET | `/api/v1/Customers/{id}` | Detail - called by `order-api` via Refit |
| POST | `/api/v1/Customers` | Create a customer profile, given an existing `UserId` from `identity-api` |
| PUT | `/api/v1/Customers/{id}` | Update |

### Tasks

- [x] `Customer` model with a plain `UserId` column (no FK to `identity-api`)
- [x] `AppDbContext`, DTOs, Mapster mapping, FluentValidation validator, interface-backed service (side by side in `Services/`), controller
- [x] Depends on `Common.Lib`
- [x] Added to `src/AppHost/AppHost.cs`: `builder.AddProject<Projects.Customer_API>("customer-api").WithReference(customerDb)`
- [x] Tests, including one confirming `customer-api` never attempts a direct database call against `identity-api`'s schema

## feature/order-api

The only service that calls others synchronously, and the origin of the tutorial's asynchronous flow. Depends on `catalog-api` and `customer-api` being registered.

### Endpoints (`/api/v1/Orders/**`)

| Method | URL | Description |
|---|---|---|
| GET | `/api/v1/Orders/{id}` | Detail, enriched with product/customer data resolved via Refit |
| POST | `/api/v1/Orders` | Create - requires an `Idempotency-Key` header; validates `CustomerId`/`ProductId` synchronously; publishes `OrderCreatedEvent` asynchronously after commit |
| GET | `/api/v1/Orders` | Paginated list |

### Tasks

- [x] `Order` model (plain `CustomerId`/`ProductId` columns, `Status` defaulting to `Pending`), `IdempotencyKey` model
- [x] `IProductClient`, `ICustomerClient` (Refit interfaces, resolved through `Microsoft.Extensions.ServiceDiscovery`)
- [x] **Idempotent creation**: `POST /api/v1/Orders` requires an `Idempotency-Key` header; before doing anything else, the service checks whether that key already exists in `idempotency_keys` - if so, it returns the previously created order instead of creating a duplicate (covers Refit's own retry-on-timeout behavior, and clients retrying after a dropped connection)
- [x] `AddEntityFrameworkOutbox<AppDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); })` configured on the MassTransit registration - see [The Saga in detail](#the-saga-in-detail-order-confirmation); `OrderService.CreateAsync` publishes `OrderCreatedEvent` via `IPublishEndpoint.Publish(...)` immediately after building the order, in the same `SaveChangesAsync()` call that persists it - the outbox is what turns that into one atomic operation instead of a race
- [x] `order-api`: validates via both Refit clients, computes `Total`, persists the order as `Pending`, the idempotency key, and the outbox message all in the **same local transaction** - MassTransit relays the event to Kafka only once that transaction has actually committed, never before (never publish before commit, or a consumer could react to an order that turns out not to exist)
- [x] `OrderConfirmedEventConsumer` (`Consumers/`): consumes the Saga's success event, defined once in `src/Contracts/`, and updates the order's `Status` from `Pending` to `Confirmed` - this is the only path that ever reaches `Confirmed`
- [x] `NotificationFailedEventConsumer` (`Consumers/`): consumes the Saga's compensating event, also from `src/Contracts/`, and updates the order's `Status` to `ConfirmationFailed`
- [x] Explicit handling of a Refit call failing (`ApiException`) - mapped to a clear `BusinessRuleException`/404 rather than leaking a raw HTTP exception
- [x] Depends on `Common.Lib`
- [x] Added to `src/AppHost/AppHost.cs`: `builder.AddProject<Projects.Order_API>("order-api").WithReference(orderDb).WithReference(kafka).WithReference(catalogService).WithReference(customerService)` - the `.WithReference` calls to `catalogService`/`customerService` are what makes Aspire's service discovery resolve `https+http://catalog-api`/`https+http://customer-api` for this service's Refit clients
- [x] Tests: WireMock.Net stubs for `IProductClient`/`ICustomerClient` (success and failure), a repeated `POST` with the same `Idempotency-Key` returning the same order instead of creating a second one, a Testcontainers Kafka broker verifying `OrderCreatedEvent` lands in `OutboxMessage` within the same transaction as the order and is only relayed to Kafka after that transaction commits, and both consumers correctly transitioning `Status`

## feature/notification-worker

New service - this tutorial's only consumer-only, database-less microservice, the single point through which every outbound e-mail leaves the system (order confirmations *and* account e-mails), and the second half of the choreographed Saga.

### Tasks

- [x] `OrderCreatedEventConsumer` (MassTransit `IConsumer<OrderCreatedEvent>`): receives the event, calls `IEmailNotification.Worker` (which, for this tutorial, logs a message instead of sending a real email)
- [x] `UserRegisteredEventConsumer`: receives `UserRegisteredEvent` on the `identity-events` topic, sends the activation e-mail (logged, same as above)
- [x] `PasswordResetRequestedEventConsumer`: receives `PasswordResetRequestedEvent` on the same topic, sends the password-reset e-mail
- [x] `IEmailNotification.Worker` is shared across all three consumers - one service, one place that "sends" e-mail, regardless of which business event triggered it
- [x] On success, `OrderCreatedEventConsumer` publishes `OrderConfirmedEvent` (carrying the `OrderId`) onto `notification-events` - the Saga's nominal-path outcome, symmetric with the compensating event below, so `order-api` always eventually hears back one way or the other, never left silently `Pending`
- [x] Deliberately simulated failure path, on the order flow only: if the "notification" fails (configurable, e.g. a specific product name triggers a simulated failure for demo purposes), the consumer publishes a `NotificationFailedEvent` back onto `notification-events`, rather than silently swallowing the error
- [x] This is the tutorial's **choreographed Saga**, end to end - see [The Saga in detail](#the-saga-in-detail-order-confirmation). No central orchestrator, no distributed transaction. The account-related events are simpler: fire-and-forget, no compensating action, since a failed activation e-mail doesn't need to undo the account creation
- [x] Depends on `Common.Lib`
- [x] Added to `src/AppHost/AppHost.cs`: `builder.AddProject<Projects.Notification_Worker>("notification-worker").WithReference(kafka)` (the `kafka` resource itself, `builder.AddKafka("kafka")`, was declared once in `feature/infrastructure`)
- [x] Tests: a Testcontainers Kafka test verifying the full order-flow choreography in both directions - publish `OrderCreatedEvent` → consumer reacts → nominal case: `OrderConfirmedEvent` published → (in `order-api`'s own test suite) `Status` becomes `Confirmed`; failure case: simulated failure → `NotificationFailedEvent` published → `Status` becomes `ConfirmationFailed` - plus a simpler test confirming `UserRegisteredEvent`/`PasswordResetRequestedEvent` trigger the expected `IEmailNotification.Worker` call

## feature/api-gateway

Single entry point. Depends on every business service already being registered.

### Tasks

- [ ] `Yarp.ReverseProxy`, route definitions per service, each cluster destination expressed as an Aspire-resolved logical address (`https+http://catalog-api`, etc.) rather than a fixed host:port - no manual refresh loop needed, since every reference was already declared once in `AppHost`
- [ ] `JwtValidationMiddleware`: validates the JWT's signature/expiration on every route except `/api/v1/Auth/Register`, `/api/v1/Auth/Login`, `/api/v1/Auth/ConfirmEmail`
- [ ] `RateLimiterPolicies`: Redis-backed distributed rate limiter applied to `/api/v1/Auth/Login`, protecting `identity-api` against brute-force attempts (a fixed number of requests per second per client IP, configurable)
- [ ] Added to `src/AppHost/AppHost.cs`: `builder.AddProject<Projects.ApiGateway>("api-gateway").WithReference(redis).WithReference(identityService).WithReference(catalogService).WithReference(customerService).WithReference(orderService).WithReference(notificationService).WithExternalHttpEndpoints()` - the only service Aspire exposes outside its own network
- [ ] Tests: routing to a mocked downstream, JWT rejection on a protected route without a token, pass-through on public routes, rate limiter returning 429 past the configured threshold

## feature/observability

Distributed tracing across the whole system - arguably the single most useful addition for actually operating (and debugging) this architecture. Much of the wiring already exists from `feature/infrastructure`'s `ServiceDefaults`; this branch is mostly about verifying it and adding what `ServiceDefaults` doesn't cover by default.

### Tasks

- [ ] Confirm every service's `ServiceDefaults`-provided OpenTelemetry pipeline is actually exporting - the Aspire Dashboard (opened automatically when `AppHost` runs) should show live traces without any extra exporter configuration
- [ ] MassTransit's built-in `ActivitySource` diagnostics explicitly added to the OpenTelemetry pipeline (`ServiceDefaults`' default only instruments `HttpClient` and ASP.NET Core out of the box, not MassTransit), so Kafka hops appear in traces alongside HTTP hops
- [ ] Verify trace propagation across **both** communication styles: a single trace should show `api-gateway → order-api → catalog-api` (via Refit) as one connected trace, and a separate trace should show `order-api → notification-worker` (via the Kafka message) as connected too - this must be verified by hand once in the dashboard, not assumed
- [ ] A short walkthrough (in this branch's own notes) showing a captured trace in the Aspire Dashboard for a full order-creation request, annotated with what each span represents

## feature/resilience

Adds fault tolerance to `order-api`'s synchronous calls.

### Tasks

- [ ] `Microsoft.Extensions.Http.Resilience`, a resilience pipeline (circuit breaker + retry + timeout) attached to the `IProductClient`/`ICustomerClient` typed `HttpClient` registrations via `AddResilienceHandler`
- [ ] Fallback handlers returning a clear "product/customer service unavailable" business error instead of the order creation hanging or throwing an unhandled exception
- [ ] A deliberately induced failure test: stop `catalog-api` in the test setup, verify the circuit breaker opens after the configured failure threshold and the fallback is used
- [ ] A diagnostics endpoint (or structured log) exposing current circuit breaker state, for parity with the observability this pattern is supposed to provide

## feature/contract-testing (bonus)

Formalizes the API shape `order-api` depends on, so a breaking change in `catalog-api`/`customer-api` is caught at build time rather than discovered by `order-api`'s WireMock.Net tests going stale.

### Tasks

- [ ] `PactNet` added to `catalog-api` and `customer-api` (the producers) for contract verification
- [ ] `order-api`'s test suite (the consumer) generates Pact files describing the exact shape it expects from `GET /api/v1/Catalog/Products/{id}` and `GET /api/v1/Customers/{id}`
- [ ] Each producer's build verifies itself against the consumer-generated pact files, failing CI if its response shape diverges
- [ ] Document the trade-off honestly: contract testing only replaces the *shape* verification WireMock.Net was doing; it doesn't replace `feature/resilience`'s failure-handling tests, which still need hand-written failure scenarios

## Order of work

1. `feature/common-lib` → Pull Request to `develop`
2. `feature/infrastructure` (depends on `common-lib`, needed before any business service can register or fetch config) → Pull Request to `develop`
3. `feature/identity-api` (depends on `common-lib`, `infrastructure`) → Pull Request to `develop`
4. `feature/catalog-api` (depends on `common-lib`, `infrastructure`) → Pull Request to `develop`
5. `feature/customer-api` (depends on `common-lib`, `infrastructure`) → Pull Request to `develop`
6. `feature/order-api` (depends on `catalog-api`, `customer-api`) → Pull Request to `develop`
7. `feature/notification-worker` (depends on `order-api` publishing `OrderCreatedEvent` and `identity-api` publishing `UserRegisteredEvent`/`PasswordResetRequestedEvent`) → Pull Request to `develop`
8. `feature/api-gateway` (depends on every business service) → Pull Request to `develop`
9. `feature/observability` (depends on every service existing, touches all of them) → Pull Request to `develop`
10. `feature/resilience` (depends on `order-api`) → Pull Request to `develop`
11. `feature/contract-testing` (bonus, depends on `catalog-api`, `customer-api`, `order-api`) → Pull Request to `develop`
12. `develop` → `master`

## Code conventions

- Namespace root per service matches its project name exactly (`Identity.API`, `Catalog.API`, `Customer.API`, `Order.API`, `Notification.Worker`, `ApiGateway`) - no solution-wide prefix repeated on every project, matching the .NET tooling default (a project's root namespace is its own name) rather than Microsoft's `DotnetMicroservicesTutorial.<ServiceName>` convention this document used before adopting the `{Domain}.API` naming Microsoft's own `dotnet/eShop` reference app uses (`Catalog.API`, `Identity.API`, `Ordering.API`, ...); `Common.Lib` and `Contracts` keep their own project names as their namespace too, for the same reason
- DTOs: C# `record` types
- **Services, C# style**: an interface (`IProductService`) and its implementation (`ProductService`) live **side by side in the same `Services/` folder**, never split into separate `Interfaces/`/`Implementations/` subfolders - splitting by "is it an interface or a class" is a Java package-by-type habit, not how C# projects are organized. The interface still exists (mainly so Moq can substitute it in a controller/consumer unit test), but physically separating it from its one implementation buys nothing
- **MassTransit message contracts live in their own shared `Contracts` project**, referenced by both the publishing and the consuming service - the standard MassTransit convention, so a producer and its consumers can never silently drift onto two different shapes of the "same" event. `Contracts` holds nothing else - no business logic, no EF Core entity, exactly as strict a rule as `Common.Lib`'s
- Every route follows the default ASP.NET Core convention (`[Route("api/v1/[controller]")]`), controller- and action-derived segments left in their C# casing (PascalCase) - no manual kebab-case or lowercase route strings anywhere
- No service ever queries another service's database directly - cross-service data access always goes through that service's API, synchronously (Refit) or asynchronously (Kafka/MassTransit), never a shared connection string
- IDs referencing another service's entity (`UserId`, `CustomerId`, `ProductId`) are plain columns, never foreign keys
- `Common.Lib` contains only cross-cutting infrastructure (exceptions, `ProblemDetails` mapping, logging) - never an EF Core entity, a repository, or a business rule
- Every event published on Kafka is named in the past tense (`OrderCreatedEvent`, `NotificationFailedEvent`) and carries only the IDs and data a consumer actually needs, never a full internal entity
- Conventional Commits, one commit per file added or modified

## Concepts covered

- Microservices decomposition from a domain model, and where to draw service boundaries
- Database-per-service, and why cross-service foreign keys are avoided
- Naming and applying standard microservices design patterns deliberately, not by accident (see [Design patterns used](#design-patterns-used))
- A deliberately shared project (`Common.Lib`) with an explicit, enforced rule about what belongs in it - and why that's viable specifically because this is a monorepo
- Local orchestration and service discovery with .NET Aspire (`AppHost`, `ServiceDefaults`), and why that's a different trade-off than a standalone service registry
- Client-side load balancing, used transparently by both Refit and the gateway
- Declarative synchronous inter-service calls with Refit
- Asynchronous, event-driven inter-service communication with Kafka via MassTransit
- A choreographed Saga with two symmetric outcome events, and why it doesn't need a distributed transaction
- The Transactional Outbox pattern, closing the gap between "persist" and "publish" atomically (MassTransit's built-in EF Core outbox, not a hand-rolled one)
- Idempotent request handling (`Idempotency-Key`) as a practical answer to retries in a distributed system
- API Gateway routing and centralized JWT validation with YARP
- Rate limiting at the gateway (Redis-backed)
- Distributed tracing with OpenTelemetry .NET across both synchronous and asynchronous calls
- Circuit breakers and fallbacks with `Microsoft.Extensions.Http.Resilience`
- Consumer-driven contract testing with PactNet (bonus)
- Health check liveness/readiness groups
- Testing a service in isolation from its dependencies (WireMock.Net stubs, Testcontainers Kafka)
- Per-service CI pipelines in a monorepo, with a shared project as a build dependency
- Containerization of a multi-service system, orchestrated locally by Aspire and generated for deployment via `aspire publish`

## How to follow this tutorial

1. Clone the repository (`git clone https://github.com/EdgarEldy/dotnet-microservices-tutorial.git`) and check out `develop`
2. Follow the branches in order: `feature/common-lib` → `feature/infrastructure` → `feature/identity-api` → `feature/catalog-api` → `feature/customer-api` → `feature/order-api` → `feature/notification-worker` → `feature/api-gateway` → `feature/observability` → `feature/resilience` → (bonus) `feature/contract-testing`
3. Run the whole system locally: `dotnet run --project src/AppHost` - this starts PostgreSQL, Kafka, Redis, and every service together, and opens the Aspire Dashboard automatically
4. Access the system exclusively through the gateway: the Dashboard's Resources tab shows `api-gateway`'s actual assigned URL for this run
5. Use the Dashboard for everything observability-related (traces, structured logs, metrics, and each resource's live status) - no separate UI to open; each service's own Swagger UI is reachable individually from the same Resources tab during development
