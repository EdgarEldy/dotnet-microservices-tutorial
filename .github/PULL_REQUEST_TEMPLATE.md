## Branch

`feature/<name>`

## Task checklist

<!-- Copy the relevant feature/<name> section from README.md's task checklist here, checked off. -->

- [ ]

## Commit summary

<!-- One line per meaningful commit, in the order they were made. -->

## Test checklist

- [ ] `dotnet build` and `dotnet test` pass for every project touched by this branch
- [ ] Unit tests added/updated for new logic (xUnit, Moq)
- [ ] Integration tests added where applicable (WebApplicationFactory, Testcontainers PostgreSQL/Kafka, WireMock.Net, Aspire.Hosting.Testing)
- [ ] Every test method follows `MethodName_ShouldExpectedOutcome_WhenCondition`
- [ ] Manually verified through `dotnet run --project src/AppHost` where the change is HTTP- or message-facing

## Code review checklist

- [ ] `builder.AddServiceDefaults()` is the first call in every touched `Program.cs`
- [ ] Every dependency on another service or resource is a `.WithReference(...)` in `AppHost.cs`, consumed through a logical name, never a hardcoded host or port
- [ ] Success responses return the resource directly (`ActionResult<T>`, correct status codes, pagination in `X-Total-Count`/`Link` headers), no generic response envelope
- [ ] Every error is a `ProblemDetails`/`ValidationProblemDetails` produced by `Common.Lib`'s `GlobalExceptionHandler`
- [ ] Time comes from an injected `TimeProvider`, never `DateTime.UtcNow`
- [ ] Routes use the default `[Route("api/v1/[controller]")]` convention, PascalCase, nothing hand-cased
- [ ] Interface and implementation side by side in `Services/`; no business mutation or permission check bypasses the service layer
- [ ] No business logic, EF Core entity, or domain-specific DTO added to `Common.Lib` or `Contracts`
- [ ] No cross-service foreign key or direct database access across a service boundary; IDs referencing another service's entity are plain columns
- [ ] Events (if any) named in the past tense, published through the EF Core outbox (`AddEntityFrameworkOutbox`) in the same transaction as the change that produced them
- [ ] Commits: one per file, Conventional Commits with the module as scope, no em dash, no AI attribution
