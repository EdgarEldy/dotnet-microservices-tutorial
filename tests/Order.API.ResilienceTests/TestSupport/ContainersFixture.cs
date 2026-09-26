using System.Globalization;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Order.API.ResilienceTests.TestSupport.ContainersFixture))]

// Every test measures a circuit breaker against real time (break durations, timeouts): running
// them one after the other keeps those timings free of contention with another test's host.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Order.API.ResilienceTests.TestSupport;

/// <summary>
/// The infrastructure order-api needs to start (order-db for EF Core, the outbox and the SQL
/// transport; Kafka for the rider), one PostgreSQL 16 and one Kafka container for the whole run.
/// Each test starts its own order-api host on top, so each gets fresh circuit breakers.
/// </summary>
public sealed class ContainersFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.12").Build();

    public string DatabaseConnectionString => _postgres.GetConnectionString();

    public string KafkaBootstrapServers { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());

        var bootstrap = new Uri(_kafka.GetBootstrapAddress());
        KafkaBootstrapServers = $"{bootstrap.Host}:{bootstrap.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    public async ValueTask DisposeAsync()
    {
        await _kafka.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
