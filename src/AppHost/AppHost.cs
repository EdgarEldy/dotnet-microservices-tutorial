var builder = DistributedApplication.CreateBuilder(args);

// One PostgreSQL server, one database per business service: each service only ever
// receives the connection string of its own database (database per service).
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

var identityDb = postgres.AddDatabase("identity-db");
var catalogDb = postgres.AddDatabase("catalog-db");
var customerDb = postgres.AddDatabase("customer-db");
var orderDb = postgres.AddDatabase("order-db");

// Kafka in KRaft mode, the broker behind the order-events, notification-events and
// identity-events topics.
var kafka = builder.AddKafka("kafka");

// Each business service is added below, in its own feature branch, with
// .WithReference(...) to the resources it uses.

builder.Build().Run();
