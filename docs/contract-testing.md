# Contract testing (bonus)

`order-api` calls two other services synchronously through Refit: `catalog-api`
(`GET /api/v1/Catalog/Products/{id}`) and `customer-api` (`GET /api/v1/Customers/{id}`). Each of
them owns its response DTO and never shares it, so nothing in the compiler stops a producer from
renaming a field `order-api` reads. Consumer-driven contracts with [PactNet](https://github.com/pact-foundation/pact-net)
(MIT) close that gap.

## How it runs

| Step | Where | What it proves |
|---|---|---|
| 1. Consumer | `tests/Order.API.ContractTests` | `order-api`'s real Refit clients (`IProductClient`, `ICustomerClient`, registered like in `Extensions.cs`, with `ForwardAccessTokenHandler`) talk to a Pact mock server. The expectations (path, forwarded `Bearer` token, status, content type, body fields matched on their *type*) are written to `pacts/order-api-catalog-api.json` and `pacts/order-api-customer-api.json`. |
| 2. Committed contract | `pacts/` | The pact files are versioned with the code. `ci-order-api.yml` regenerates them from scratch and fails if they differ from the committed ones, so a consumer change must commit its new contract. |
| 3. Producers | `tests/Catalog.API.Tests/PactTests`, `tests/Customer.API.Tests/PactTests` | The real service (full pipeline, JWT validation, PostgreSQL through Testcontainers) runs on Kestrel; the Pact verifier replays every interaction of the committed file against it. A `POST /provider-states` endpoint, added by the test host only, seeds the data each interaction needs ("a product with id 1 exists", "no customer with id 999", ...), and the verifier sends a token signed with the test key (for customers, the token of the profile's owner). `ci-catalog-api.yml`/`ci-customer-api.yml` also run when their pact file changes. |

## What it replaces, and what it does not

- It replaces the **shape** verification that `order-api`'s WireMock.Net stubs were implicitly
  doing. A stub only encodes what `order-api` *believes* the producer returns; nothing ever
  checked that belief against the producer. The pact is the same belief, but the producer's own
  build now verifies it.
- It does **not** replace `feature/resilience`'s failure-handling tests. A contract describes a
  successful exchange and the documented 404; it says nothing about timeouts, 5xx storms, an open
  circuit breaker or a producer that is down. Those still need hand-written failure scenarios
  (WireMock.Net, stopped containers), and they still belong to `order-api`'s own test suite.
- It checks what `order-api` reads, not everything a producer returns: a producer may add fields
  freely; it may not remove, rename or retype one the consumer uses.

## No Pact Broker, on purpose

The contracts are files in the repository instead of a [Pact Broker](https://docs.pact.io/pact_broker).
In a monorepo where consumer and producers change in the same pull request, the pull request
itself is the coordination point, and there is no extra service to run. What a broker would add
once services live in separate repositories and deploy independently:

- **Versioned contracts per consumer version and branch**, instead of one file on one branch.
- **Verification results published back**, so each side knows which versions are compatible.
- **`can-i-deploy`**: a deployment gate that answers "is this producer version compatible with
  every consumer version currently in production?", which a file in a repository cannot answer.
- **Webhooks** that trigger the producer's verification as soon as a consumer publishes a changed
  contract, and **pending/WIP pacts** so a new expectation does not break the producer's build
  before it has had a chance to implement it.
