// Every test class owns its PostgreSQL container, but Mapster's TypeAdapterConfig.GlobalSettings
// is process-wide: each WebApplicationFactory host re-scans it at startup, which would race with a
// query compiled by another class running in parallel. Classes therefore run one after the other.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
