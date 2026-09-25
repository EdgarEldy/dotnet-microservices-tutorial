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
    public void AddKafka_ShouldDeclareKafkaServer_WhenAppHostIsBuilt()
    {
        var kafka = Assert.Single(_builder.Resources.OfType<KafkaServerResource>());

        Assert.Equal("kafka", kafka.Name);
    }
}
