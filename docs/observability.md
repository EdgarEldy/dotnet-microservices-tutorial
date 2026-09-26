# Observability walkthrough: one order, followed across every hop

This page is the `feature/observability` walkthrough the README asks for: how to run the
whole system, where to look in the Aspire Dashboard, and the two traces that prove context
propagation works for **both** communication styles (synchronous HTTP through Refit, and
asynchronous Kafka messages through MassTransit).

The captured traces are verified by hand, once, in the dashboard. Every section marked
**TO VERIFY BY HAND, results pending** is where the captured trace (span tree, durations,
screenshot or copied text) gets pasted after that run.

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

1. `POST /api/v1/Auth/Register`, confirm the e-mail (the link is in identity-api's logs),
   then `POST /api/v1/Auth/Login`: keep the access token.
2. With an Admin token: create a category and a product in catalog-api
   (`POST /api/v1/Catalog/Categories`, `POST /api/v1/Catalog/Products`), or use the seeded
   sample data.
3. With the user's token: `POST /api/v1/Customers` to create the caller's customer profile.
4. `POST /api/v1/Orders` with an `Idempotency-Key` header, the `CustomerId` and one or more
   `ProductId`s. This single request produces both traces below.

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

**TO VERIFY BY HAND, results pending**: paste the captured trace A (span tree, trace ID,
durations) here, and note any difference with the expected tree above.

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

**TO VERIFY BY HAND, results pending**: paste the captured trace B (span tree, trace ID,
whether it is the same trace as A or a linked one, durations) here, for the nominal path. Then
repeat with the simulated failure trigger and paste the failure path's tree.

## Checklist for the hand verification

- [ ] Every resource (identity-api, catalog-api, customer-api, order-api, notification-worker,
      api-gateway) shows traces, logs and metrics in the dashboard with no extra exporter
      configuration.
- [ ] No `/health/live` or `/health/ready` request appears in Traces.
- [ ] Trace A: api-gateway, order-api, catalog-api and customer-api spans in one trace.
- [ ] Trace B: order-api produce, notification-worker consume, outcome produce and order-api
      consume connected by trace context, nominal path.
- [ ] Trace B, simulated failure path: `NotificationFailedEvent` and the
      `ConfirmationFailed` update connected the same way.
- [ ] MassTransit metrics (`messaging.masstransit.*`) visible for order-api and
      notification-worker.
