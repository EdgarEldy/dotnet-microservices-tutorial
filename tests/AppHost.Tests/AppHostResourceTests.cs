using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace AppHost.Tests;

// Inspects the application model AppHost.cs declares, without starting it: no Docker needed.
public sealed class AppHostResourceTests : IAsyncLifetime
{
    private IDistributedApplicationTestingBuilder _builder = null!;

    public async ValueTask InitializeAsync()
    {
        _builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _builder.DisposeAsync();
    }

    [Fact]
    public void AddPostgres_ShouldDeclarePostgresServer_WhenAppHostIsBuilt()
    {
        var server = Assert.Single(_builder.Resources.OfType<PostgresServerResource>());

        Assert.Equal("postgres", server.Name);
    }

    [Fact]
    public void WithDataVolume_ShouldMountVolumeOnPostgresDataDirectory_WhenAppHostIsBuilt()
    {
        var server = Assert.Single(_builder.Resources.OfType<PostgresServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        // The official image moved its data directory in PostgreSQL 18: the volume must
        // target the one matching the image actually run, or data is not persisted.
        var major = int.Parse(image.Tag!.Split('.', '-')[0], System.Globalization.CultureInfo.InvariantCulture);
        var expectedTarget = major >= 18 ? "/var/lib/postgresql" : "/var/lib/postgresql/data";

        var mount = Assert.Single(server.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(ContainerMountType.Volume, mount.Type);
        Assert.Equal(expectedTarget, mount.Target);
    }

    [Fact]
    public void AddPostgres_ShouldUsePostgreSql16Image_WhenAppHostIsBuilt()
    {
        // README "Tech stack": PostgreSQL 16.
        var server = Assert.Single(_builder.Resources.OfType<PostgresServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal("postgres", image.Image.Split('/')[^1]);
        Assert.Matches(@"^16(\.|-|$)", image.Tag);
    }

    [Theory]
    [InlineData("identity-db")]
    [InlineData("catalog-db")]
    [InlineData("customer-db")]
    [InlineData("order-db")]
    public void AddDatabase_ShouldDeclareDatabaseOnPostgresServer_WhenAppHostIsBuilt(string databaseName)
    {
        var database = Assert.Single(
            _builder.Resources.OfType<PostgresDatabaseResource>(),
            d => d.Name == databaseName);

        Assert.Equal(databaseName, database.DatabaseName);
        Assert.Equal("postgres", database.Parent.Name);
        Assert.Contains(databaseName, database.Parent.Databases.Keys);
    }

    [Fact]
    public void AddDatabase_ShouldDeclareFourDistinctDatabases_WhenAppHostIsBuilt()
    {
        var databases = _builder.Resources.OfType<PostgresDatabaseResource>().ToList();

        Assert.Equal(4, databases.Count);
        Assert.Equal(4, databases.Select(d => d.DatabaseName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            ["catalog-db", "customer-db", "identity-db", "order-db"],
            databases.Select(d => d.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AddProject_ShouldReferenceOnlyCustomerDb_WhenCustomerApiIsDeclared()
    {
        // README (feature/customer-api): customer-api never reaches identity-api's database, so
        // AppHost hands it no connection string but its own.
        var customerApi = Assert.Single(_builder.Resources.OfType<ProjectResource>(), r => r.Name == "customer-api");

        var referencedDatabases = customerApi.Annotations.OfType<ResourceRelationshipAnnotation>()
            .Where(a => a.Type == "Reference")
            .Select(a => a.Resource)
            .OfType<PostgresDatabaseResource>()
            .Select(d => d.Name)
            .Distinct()
            .ToList();

        Assert.Equal(["customer-db"], referencedDatabases);
    }

    [Theory]
    [InlineData("redis")]
    [InlineData("identity-api")]
    [InlineData("catalog-api")]
    [InlineData("customer-api")]
    public void AddProject_ShouldReferenceResource_WhenApiGatewayIsDeclared(string resourceName)
    {
        // README (feature/api-gateway): the gateway reaches Redis and every business service
        // through AppHost references, never through a hardcoded address.
        var gateway = Assert.Single(_builder.Resources.OfType<ProjectResource>(), r => r.Name == "api-gateway");

        var referenced = gateway.Annotations.OfType<ResourceRelationshipAnnotation>()
            .Where(a => a.Type == "Reference")
            .Select(a => a.Resource.Name);

        Assert.Contains(resourceName, referenced);
    }

    [Fact]
    public void WithExternalHttpEndpoints_ShouldExposeOnlyApiGateway_WhenAppHostIsBuilt()
    {
        // README (feature/api-gateway): the only service Aspire exposes outside its own network.
        var externallyExposed = _builder.Resources.OfType<ProjectResource>()
            .Where(r => r.Annotations.OfType<EndpointAnnotation>().Any(e => e.IsExternal))
            .Select(r => r.Name);

        Assert.Equal(["api-gateway"], externallyExposed);
    }

    [Fact]
    public void AddRedis_ShouldDeclareRedisServer_WhenAppHostIsBuilt()
    {
        var redis = Assert.Single(_builder.Resources.OfType<RedisResource>());

        Assert.Equal("redis", redis.Name);
    }

    [Fact]
    public void AddKafka_ShouldDeclareKafkaServer_WhenAppHostIsBuilt()
    {
        var kafka = Assert.Single(_builder.Resources.OfType<KafkaServerResource>());

        Assert.Equal("kafka", kafka.Name);
    }
}
