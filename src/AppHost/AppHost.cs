var builder = DistributedApplication.CreateBuilder(args);

// One PostgreSQL server, one database per business service: each service only ever
// receives the connection string of its own database (database per service).
// PostgreSQL 16, as the README specifies (Aspire's default image is newer). The tag must be
// set before WithDataVolume(), which picks the data directory to mount from the image version.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("16")
    .WithDataVolume();

var identityDb = postgres.AddDatabase("identity-db");
var catalogDb = postgres.AddDatabase("catalog-db");
var customerDb = postgres.AddDatabase("customer-db");
var orderDb = postgres.AddDatabase("order-db");

// Kafka in KRaft mode, the broker behind the order-events, notification-events and
// identity-events topics.
var kafka = builder.AddKafka("kafka");

// The HMAC-SHA256 key identity-api signs its JWTs with. Generated on the first run and persisted
// in this project's user secrets, so no key is ever committed. Every service that validates those
// tokens (api-gateway, and the business services as they are added) receives this same parameter
// as Jwt__SigningKey, and validates tokens on its own without calling identity-api.
var jwtSigningKey = builder.AddParameter(
    "jwt-signing-key",
    new GenerateParameterDefault { MinLength = 64, Special = false },
    secret: true,
    persist: true);

// Each business service is added below, in its own feature branch, with
// .WithReference(...) to the resources it uses.
builder.AddProject<Projects.Identity_API>("identity-api")
    .WithReference(identityDb)
    .WaitFor(identityDb)
    .WithReference(kafka)
    .WaitFor(kafka)
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey);

var catalogService = builder.AddProject<Projects.Catalog_API>("catalog-api")
    .WithReference(catalogDb)
    .WaitFor(catalogDb)
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey);

builder.Build().Run();
