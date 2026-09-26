# Observability walkthrough: one order, followed across every hop

This page is the `feature/observability` walkthrough the README asks for: how to run the
whole system, where to look in the Aspire Dashboard, and the two traces that prove context
propagation works for **both** communication styles (synchronous HTTP through Refit, and
asynchronous Kafka messages through MassTransit).

The traces below were captured once, on a real run of the whole system (every service, real
PostgreSQL 16, Kafka and Redis containers), for one order placed through the api-gateway. They
were read from the Aspire Dashboard's telemetry API rather than copied from screenshots (see
"Reading traces without a browser" at the end), so the span names and durations are the real
ones.

## What is instrumented, and where

Every service calls `builder.AddServiceDefaults()` first, and `ServiceDefaults/Extensions.cs`
(`ConfigureOpenTelemetry`) wires the same pipeline everywhere:

| Signal | Sources | Why |
|---|---|---|
| Traces | ASP.NET Core (server spans, health probes filtered out) | one span per incoming HTTP request |
| Traces | `HttpClient` (client spans) | one span per outgoing call, including Refit and YARP; also injects the W3C `traceparent` header |
| Traces | `MassTransit` ActivitySource | produce/consume spans for the outbox, the PostgreSQL transport and Kafka; MassTransit copies the trace context into the message headers |
| Traces | Npgsql / EF Core (added by the Aspire `Npgsql.EntityFrameworkCore.PostgreSQL` client integration) | one span per SQL command |
| Traces | the service's own ActivitySource (named after the application) | custom spans, if any |
| Metrics | ASP.NET Core, `HttpClient`, .NET runtime, `MassTransit` meter | request rates and durations, GC and thread pool, message send/consume counts and durations |
| Logs | every `ILogger` call, with formatted message and scopes | structured logs, correlated to the trace through the trace and span IDs |

The exporter is OTLP, switched on only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. AppHost sets
it for every project it runs, pointing at the Aspire Dashboard, so no service has any exporter
configuration of its own.

## Running the system

Docker Desktop must be running (PostgreSQL 16 and Kafka run as containers).

The usual command is:

```bash
dotnet run --project src/AppHost
```

On a machine where the ASP.NET Core HTTPS development certificate is not trusted, that command
stops at "Trusting certificates" (trusting it needs a Windows confirmation dialog). The
workaround is to build once, then start the AppHost executable with the `http` launch profile's
environment variables, from the `src/AppHost` folder (PowerShell):

```powershell
dotnet build src/AppHost
cd src/AppHost
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DOTNET_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:15225"
$env:ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL = "http://localhost:19220"
$env:ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL = "http://localhost:20289"
$env:ASPIRE_ALLOW_UNSECURED_TRANSPORT = "true"
.\bin\Debug\net10.0\AppHost.exe
```

The console prints the dashboard login URL (`http://localhost:15225/login?t=<token>`). Stop
everything with `Ctrl+C`, or `Stop-Process -Name AppHost` from another terminal.

Wait until every resource shows **Running** (and healthy) on the **Resources** page before
sending requests: services wait for their database and Kafka through `.WaitFor(...)`.

## Where to look in the Aspire Dashboard

- **Resources**: every container and project, its state, its endpoints (the api-gateway URL
  to call is here) and its environment variables (the injected connection strings and
  `services__*` service discovery entries).
- **Traces**: one row per trace, with the resources it touched and its total duration. Filter
  by resource (for example `order-api`) or by name, then open a trace to see the span tree
  (waterfall). Spans are colored per resource, so a trace crossing four services shows four
  colors.
- **Structured logs**: every log entry with its properties. Each entry carries its `TraceId`:
  the trace link on a log line opens the trace it belongs to, and a trace's "View logs" shows
  every log emitted inside it, across services.
- **Metrics**: per resource, the instruments listed above, for example
  `http.server.request.duration`, `http.client.request.duration`, and MassTransit's
  `messaging.masstransit.*` instruments (send, consume, and their durations).

## Preparing the request

Through the api-gateway only (the address comes from the Resources page):

1. `POST /api/v1/Auth/Register`, then confirm the e-mail with
   `GET /api/v1/Auth/ConfirmEmail?userId=...&token=...`. identity-api never sends mail and
   notification-worker only logs a truncated token, so read the full token from the
   `UserRegisteredEvent` itself, inside the Kafka container, on its internal listener
   (the host-mapped port is not reachable from inside the container):
   `docker exec <kafka-container> kafka-console-consumer --bootstrap-server localhost:9093
   --topic identity-events.user-registered --from-beginning --timeout-ms 10000`.
   Then `POST /api/v1/Auth/Login`: keep the access token.
2. With an Admin token: create a category and a product in catalog-api
   (`POST /api/v1/Catalog/Categories`, `POST /api/v1/Catalog/Products`), or use the seeded
   sample data.
3. With the user's token: `POST /api/v1/Customers` to create the caller's customer profile.
4. `POST /api/v1/Orders` with an `Idempotency-Key` header and a body
   `{"customerId": ..., "productId": ..., "quantity": ...}`. This single request produces
   both traces below.

## Trace A: synchronous hops (api-gateway to order-api to catalog-api and customer-api)

What proves propagation: every span below has the **same trace ID**, and each child's parent
span ID is the span above it. The `HttpClient` instrumentation injects the W3C `traceparent`
header on every outgoing call (YARP's proxied request and the Refit clients' requests), and
the ASP.NET Core instrumentation of the receiving service reads it, so the receiving server
span becomes a child of the calling client span.

Expected span tree (names are the OpenTelemetry HTTP semantic conventions: `METHOD route`):

```
api-gateway   POST /api/v1/Orders                     server  (YARP route, JWT validated here)
  api-gateway   POST                                  client  (YARP forwards to order-api)
    order-api     POST api/v1/Orders                  server  (OrdersController.Create)
      order-api     postgres SELECT idempotency_keys  db      (idempotency check)
      order-api     GET                               client  (Refit IProductClient, per product)
        catalog-api   GET api/v1/Catalog/Products/{id}  server
          catalog-api   postgres SELECT products        db
      order-api     GET                               client  (Refit ICustomerClient)
        customer-api  GET api/v1/Customers/{id}       server
          customer-api  postgres SELECT customers     db
      order-api     OrderCreatedEvent publish         producer (MassTransit, lands in the outbox)
      order-api     postgres INSERT orders, ...       db      (Order + IdempotencyKey + OutboxMessage, one transaction)
```

What each span represents:

- **api-gateway server span**: the client's request as the gateway received it. Its duration
  is the end-to-end latency the client sees.
- **api-gateway client span**: YARP's forwarded request to order-api, resolved through
  service discovery (`https+http://order-api`).
- **order-api server span**: the controller action; everything order-api does for this
  request is nested under it.
- **Refit client spans**: the synchronous validation calls; their duration shows the cost of
  the API Composition, and a failure (4xx/5xx, circuit open once `feature/resilience` lands)
  shows up as an error status on that span.
- **catalog-api / customer-api server spans**: the downstream work, children of the Refit
  client span that called them, even though they run in another process.
- **database spans**: every SQL command, from the Npgsql instrumentation that the Aspire
  client integration registers.
- **MassTransit publish span**: `IPublishEndpoint.Publish` inside the request. With the
  transactional outbox it does not reach Kafka here: it writes an `OutboxMessage` row, carrying
  the current trace context in its headers, committed by the same `SaveChangesAsync` as the
  order.

### Captured (2026-09-26)

One `POST /api/v1/Orders` through the gateway (the first order after startup, so the durations
include cold-start work) produced trace `8a050b50ed3378e45b8564bbf3c0e31e`. Its synchronous part:

```
api-gateway   POST /api/v1/Orders/{**catch-all}      server  4420 ms
  api-gateway   POST                                 client  4419 ms  (YARP to order-api)
    order-api     POST api/v1/Orders                 server  4371 ms
      order-api     postgresql                       db         8 ms  (idempotency check)
      order-api     GET                              client    63 ms  (Refit IProductClient)
        catalog-api   GET api/v1/Catalog/Products/{id:int}  server  43 ms
          catalog-api   postgresql                   db         3 ms
      order-api     GET                              client   142 ms  (Refit ICustomerClient)
        customer-api  GET api/v1/Customers/{id:int}  server   137 ms
          customer-api  postgresql                   db         5 ms
      order-api     postgresql (x4)                  db               (order, key, outbox, commit)
      order-api     outbox send                      producer  83 ms  (MassTransit, writes OutboxMessage)
```

Differences with the expected tree above:

- Span names follow the route templates as registered: the gateway's server span is named after
  the YARP route (`POST /api/v1/Orders/{**catch-all}`), the services' after their controller
  routes (`api/v1/Orders`, `{id:int}` constraints included). Npgsql names every database span
  `postgresql`; the SQL text is in the `db.query.text` attribute.
- The publish span is MassTransit's `outbox send`, not a `publish` span: with the bus outbox, the
  publish writes the `OutboxMessage` row inside the request, exactly as intended.
- Most of the 4.4 s is cold start inside order-api (first request after startup: JIT, EF Core
  model and Refit client creation); the downstream calls themselves took 63 ms and 142 ms.

## Trace B: asynchronous hops (order-api to Kafka to notification-worker and back)

What proves propagation: the Saga's spans share the trace ID of the order creation request
even though no HTTP call links them. MassTransit writes the trace context into the message
headers (outbox row, PostgreSQL transport message, Kafka record headers), and the consuming
side starts its consume span as a child of the producing span. Depending on how the dashboard
groups them, these spans appear under trace A (same trace ID) or, if a hop starts a new root,
as a separate trace linked to it: record which of the two happens.

Expected hops, in order:

```
order-api     outbox delivery: send to the PostgreSQL transport queue       producer
order-api     OrderCreatedEvent relay consume (Messaging/*KafkaRelay)       consumer (SQL transport)
  order-api     order-events.order-created send                             producer (Kafka)
notification-worker  order-events.order-created receive / process           consumer (Kafka)
  notification-worker  "e-mail" sent (log entry) or simulated failure
  notification-worker  notification-events.order-confirmed send             producer (Kafka)
                       (or notification-events.notification-failed on the failure path)
order-api     notification-events.order-confirmed receive / process         consumer (Kafka)
  order-api     postgres UPDATE orders SET status = 'Confirmed'             db
                (or 'ConfirmationFailed' on the failure path)
```

What each span represents:

- **Outbox delivery send**: MassTransit's outbox delivery service, in the background, sends the
  committed `OutboxMessage` to the bus (the MassTransit SQL transport on order-api's own
  PostgreSQL database). This is the hop that guarantees no event is lost: it only runs after
  the order's transaction committed.
- **Relay consume + Kafka send**: order-api's Kafka relay consumer takes the message from the
  SQL transport queue and produces it on the `order-events.order-created` topic; it acks the
  queue message only after Kafka accepted the record, so the Kafka send span is a child of the
  relay's consume span.
- **notification-worker consume**: the Kafka rider receives the record and runs
  `OrderCreatedEventConsumer`; the "e-mail" appears as a structured log entry attached to this
  span (Structured logs, filtered by the trace ID).
- **Outcome send**: `OrderConfirmedEvent` (nominal path) or `NotificationFailedEvent`
  (simulated failure) produced inside the consume context, on its own topic.
- **order-api outcome consume**: `OrderConfirmedEventConsumer` or
  `NotificationFailedEventConsumer` moves the order from `Pending` to `Confirmed` or
  `ConfirmationFailed`; the database span is the status update.

The time gap between the order creation response and the outbox delivery span is expected:
it is the outbox polling delay, and it is exactly what "the event is published only after the
commit" looks like on a timeline.

### Captured (2026-09-26), nominal path

The asynchronous hops are **in the same trace as trace A** (`8a050b50...`), not a separate
linked trace: the trace context survived the outbox row, the PostgreSQL transport queue and
both Kafka topics, so the whole Saga is one waterfall starting at the client's request.

```
order-api            outbox process                             consumer  73 ms  (outbox delivery, after commit)
order-api            Contracts:OrderCreatedEvent send           producer  50 ms  (to the PostgreSQL transport queue)
order-api            OrderEventsKafkaRelay receive              consumer 122 ms
order-api            OrderEventsKafkaRelay process              consumer  69 ms
  order-api            order-events.order-created send          producer  32 ms  (Kafka)
notification-worker  notification-worker receive                consumer 115 ms  (Kafka, group notification-worker)
notification-worker  notification-worker process                consumer 102 ms  (e-mail logged)
  notification-worker  notification-events.order-confirmed send producer  32 ms  (Kafka)
order-api            order-api receive                          consumer  56 ms  (Kafka, group order-api)
order-api            order-api process                          consumer  49 ms
  order-api            postgresql                               db         4 ms  (status Pending to Confirmed)
```

The order read back through the gateway went from `Pending` (the 201 response) to `Confirmed`
about 10 seconds later, and `GET /api/v1/Orders/{id}` returned it with the product and the
customer filled in by the two Refit calls.

MassTransit names its Kafka spans after the topic on the producing side and after the consumer
group on the receiving side (`notification-worker receive`, `order-api receive`).

**Failure path not captured on this run.** Triggering it needs a product named "Broken Product",
and creating one needs an Admin token (`CATALOG:WRITE`); the system seeds the Admin role but no
Admin account, so this run did not exercise it. It is covered end to end, with a real Kafka
broker, by `Notification.Worker.Tests` (the `NotificationFailedEvent` choreography) and
`Order.API.Tests` (the `ConfirmationFailed` transition); the trace shape is the same as above,
with `notification-events.notification-failed` in place of `notification-events.order-confirmed`.

## Checklist for the hand verification

- [x] Every resource (identity-api, catalog-api, customer-api, order-api, notification-worker,
      api-gateway) shows traces, logs and metrics in the dashboard with no extra exporter
      configuration (all six report traces, logs and metrics).
- [x] No `/health/live` or `/health/ready` request appears in Traces (0 health spans among the
      271 captured).
- [x] Trace A: api-gateway, order-api, catalog-api and customer-api spans in one trace.
- [x] Trace B: order-api produce, notification-worker consume, outcome produce and order-api
      consume connected by trace context, nominal path (same trace as A).
- [ ] Trace B, simulated failure path: not captured on this run (no Admin account to create
      the "Broken Product"); covered by the Kafka integration tests, see above.
- [ ] MassTransit metrics (`messaging.masstransit.*`): order-api and notification-worker do
      report metrics, but the telemetry API used here only serves traces, logs and resources,
      so the instrument names were not read programmatically. Check the Metrics page by hand.

## Reading traces without a browser

The dashboard exposes a telemetry HTTP API (`/api/telemetry/resources`, `/api/telemetry/traces`,
OTLP JSON), protected by an API key. With the AppHost started directly, the dashboard only reads
the settings AppHost passes to it, so the key is given through a key-per-file configuration
folder that AppHost forwards (`ASPIRE_DASHBOARD_FILE_CONFIG_DIRECTORY`): one file per setting,
named `Dashboard__Api__Enabled` (`true`), `Dashboard__Api__AuthMode` (`ApiKey`) and
`Dashboard__Api__PrimaryApiKey` (a random value, never committed), then:

```bash
curl -H "X-API-Key: <key>" "http://localhost:15225/api/telemetry/traces?limit=200"
```

Keep that folder outside the repository.
