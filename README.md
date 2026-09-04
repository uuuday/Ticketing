# Ticketing API

A .NET 10 Web API for event management, ticket purchasing, and sales reporting. Built with ASP.NET Core, Entity Framework Core (SQL Server), Serilog structured logging, and OpenTelemetry tracing and metrics.

---

## Table of Contents

- [The Interesting Problem](#the-interesting-problem)
- [Getting Started](#getting-started)
- [API Reference](#api-reference)
- [Data Model](#data-model)
- [Design Decisions & Trade-offs](#design-decisions--trade-offs)
- [Testing Strategy](#testing-strategy)
- [Scalability & Operationalizing](#scalability--operationalizing)
- [AI Collaboration and Acceleration](#ai-collaboration-and-acceleration)
- [What I'd Do Next](#what-id-do-next)

---

## The Interesting Problem

Most of this brief is CRUD. One requirement is not: **prevent overselling**.

Two people buying the last ticket at the same millisecond must not both succeed. That single constraint drove the data model, the transaction boundaries, the choice of database, and the test I care most about. Everything else in this README follows from it.

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- SQL Server (LocalDB, Express, or a full instance)
- Optional: an OTLP collector (Jaeger, Aspire Dashboard) to view traces and metrics

### 1. Clone

```powershell
git clone https://github.com/uuuday/Ticketing.git
cd Ticketing
```

### 2. Configure the connection string

`src/Ticketing.Api/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "Ticketing": "Server=(localdb)\\MSSQLLocalDB;Database=Ticketing;Trusted_Connection=True;TrustServerCertificate=True;Max Pool Size=200"
  }
}
```

Or use User Secrets to keep credentials out of source control:

```powershell
cd src/Ticketing.Api
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:Ticketing" "Server=(localdb)\MSSQLLocalDB;Database=Ticketing;Trusted_Connection=True;TrustServerCertificate=True"
```

> `Max Pool Size=200` matters for the concurrency test — with the default pool of 100, parallel requests queue in ADO.NET rather than reaching the database, and the test measures the pool instead of the inventory logic.

### 3. Apply migrations

```powershell
dotnet tool install --global dotnet-ef      # if needed
dotnet ef database update --project src/Ticketing.Api
```

### 4. Run

```powershell
dotnet run --project src/Ticketing.Api
```

Swagger UI: `https://localhost:56664/swagger`

### 5. Test

```powershell
dotnet test
```

---

## API Reference

| Method | Route | Success | Notes |
|---|---|---|---|
| POST | `/api/events` | 201 | Returns `Location` header |
| GET | `/api/events` | 200 | Paged; `pageSize` clamped to 100 |
| GET | `/api/events/{id}` | 200 | 404 if unknown |
| PUT | `/api/events/{id}` | 200 | Metadata only — not pricing tiers |
| DELETE | `/api/events/{id}` | 204 | Soft-cancels when orders exist |
| GET | `/api/events/{id}/availability` | 200 | Per-tier allocated / sold / remaining |
| POST | `/api/purchases` | 201 / 200 | **Requires `Idempotency-Key` header** |
| GET | `/api/purchases/{id}` | 200 | 404 if unknown |
| GET | `/api/reports/sales?eventId=` | 200 | Units and revenue per tier |

### Status codes

| Code | Meaning |
|---|---|
| 201 | Order created |
| 200 | Idempotent replay — the original order, nothing new created |
| 400 | `Idempotency-Key` header missing |
| 404 | Event, tier, or order not found |
| 409 | Sold out, event cancelled, or event already started |
| 422 | Validation failure |

All errors return [RFC 7807 ProblemDetails](https://datatracker.ietf.org/doc/html/rfc7807) with a correlation id and trace id. Stack traces are never returned to callers.

### Demonstrating idempotency

Run this twice with the **same** key:

```bash
curl -X POST 'https://localhost:56664/api/purchases' \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000' \
  -d '{
    "eventId": "ed1765c3-ac97-4346-bbda-ad3777a8000a",
    "customerRef": "buyer@example.com",
    "lines": [{ "pricingTierId": "3547b68c-8893-4ac0-afc1-683318ce4218", "quantity": 2 }]
  }'
```

First call returns **201**. Second returns **200** with the identical order, and `SoldQuantity` stays at 2 — not 4.

---

## Data Model

```
Event ──1:N──> PricingTier
  │                 ▲
  │                 │ (Restrict — never orphan a sold line)
  └──1:N──> Order ──1:N──> OrderLine
```

| Entity | Purpose |
|---|---|
| `Event` | Name, description, venue, date/time, total capacity, cancellation state |
| `PricingTier` | Per-tier price, `AllocatedQuantity`, `SoldQuantity` — **inventory lives here** |
| `Order` | One purchase; carries the unique `IdempotencyKey` |
| `OrderLine` | Quantity and `UnitPrice` captured at purchase time |

**Three choices worth explaining:**

**Inventory lives on `PricingTier`, not `Event`.** Allocation and contention are both per-tier. A VIP tier selling out shouldn't block General Admission sales.

**`SoldQuantity` is a counter, not `COUNT(*)` over tickets.** A counter can be claimed atomically in a single statement. A count cannot — you'd have to read, decide, then write, which is the race condition itself.

**`UnitPrice` is snapshotted onto `OrderLine`.** Tier prices change; historical orders must not.

---

## Design Decisions & Trade-offs

### 1. Preventing overselling — the central decision

The database is the authoritative source of seat ownership, and the claim is a **single atomic conditional UPDATE**:

```sql
UPDATE PricingTiers
SET SoldQuantity = SoldQuantity + @qty
WHERE Id = @tierId
  AND SoldQuantity + @qty <= AllocatedQuantity
```

Check and claim happen in one statement, so there is no window between deciding and taking. **Zero rows affected means sold out**, and the transaction rolls back.

**Alternatives considered and rejected:**

| Approach | Why rejected |
|---|---|
| Read-then-write in C# | Classic lost update. Two buyers both read 9 remaining, both write. Oversold. |
| Pessimistic lock (`UPDLOCK` / `SERIALIZABLE`) | Correct, but serialises every buyer for an event. A popular on-sale becomes a queue. |
| Optimistic concurrency (`RowVersion`) | Correct, but every loser retries — a retry storm under exactly the load that matters. |
| Distributed lock (Redis) | Adds a second source of truth and a new failure mode, to solve something one SQL statement already solves. |

**Defence in depth:** a `CHECK` constraint (`SoldQuantity >= 0 AND SoldQuantity <= AllocatedQuantity`) enforces the invariant at the storage layer. Overselling is impossible even if the application logic were wrong.

**Deadlock avoidance:** order lines are sorted by `PricingTierId` before claiming, so two concurrent multi-tier orders always take rows in the same sequence.

### 2. Idempotent purchases

`POST /api/purchases` requires an `Idempotency-Key` header, backed by a unique index on `Order.IdempotencyKey`.

This is not decoration. The failure mode that actually hurts in payment systems is a provider accepting a request and then timing out — the client retries and the customer is charged twice. A fast-path lookup handles sequential retries; catching the unique-constraint violation handles two concurrent requests carrying the same key, where one wins the insert and the other replays its result.

**Trade-off:** it pushes a requirement onto clients. That's the right trade for a financial mutation.

### 3. Result objects, not exceptions, for expected failures

`PurchaseService` returns a `PurchaseResult` with an explicit status rather than throwing. Sold out is a normal outcome of a well-formed request, not an exceptional condition. Exceptions are reserved for genuinely unexpected failures, which keeps the error signal meaningful in logs and metrics.

Controllers contain no business logic — they map a result to a status code.

### 4. Layered modular monolith

Presentation (controllers) → Application (services) → Domain (entities) → Data (EF Core).

Not microservices: there's no independent scaling or ownership need at this size, and distributing a transactional inventory claim would make the hard problem substantially harder. Boundaries are drawn so inventory could be extracted later if it warranted its own scaling profile.

**No repository abstraction over EF Core.** `DbContext` is already a unit of work and `DbSet<T>` is already a repository. Wrapping it would have blocked the raw atomic UPDATE the whole design depends on — the abstraction would have cost correctness.

### 5. Soft cancellation over hard delete

`DELETE` removes an event only when no orders exist; otherwise it sets `IsCancelled`. Hard-deleting an event with sold tickets orphans orders and destroys the audit trail. In a system handling money, that's a data-integrity bug rather than a feature.

### 6. Deliberately out of scope

Authentication and authorization · real payment gateway integration · seat-level selection · refunds and cancellations · notifications · admin UI · reservation/hold flow.

These are omissions by choice, not by time. Each is called out in [What I'd Do Next](#what-id-do-next).

---

## Testing Strategy

Tests are organised around **what can actually go wrong**, not around line coverage.

### The test that matters

```
Concurrent_purchases_never_oversell
  → seed a tier with 10 seats
  → fire 100 parallel purchase requests
  → assert exactly 10 succeed, 90 return 409, SoldQuantity == 10
```

Run against real SQL Server, not an in-memory provider — an in-memory database doesn't reproduce the concurrency semantics being tested, so it would pass while proving nothing.

### Coverage

| Area | Cases |
|---|---|
| **Concurrency** | 100 parallel buyers vs 10 seats; same key twice sequentially; same key twice in parallel |
| **Inventory edges** | Quantity exceeding remaining; tier with zero allocation; exact-last-ticket purchase |
| **Validation** | Quantity of 0 and negative; duplicate tier in one request; tier allocations exceeding venue capacity; past event date |
| **State** | Purchase against a cancelled event; purchase after event start; update of a cancelled event |
| **Null / boundary** | Missing `Idempotency-Key`; empty lines; `Guid.Empty` as id; unknown event and tier |
| **Reporting** | Event with no orders; multi-tier revenue totals; sell-through with zero allocation |

Every error path is asserted to return well-formed ProblemDetails — never a 500.

---

## Scalability & Operationalizing

### Observability

Serilog structured logging with correlation ids propagated through every log line and OpenTelemetry span, so a reported failure can be reconstructed from one identifier.

**The metric that matters most is `purchase.oversell_attempts`** — incremented whenever the atomic UPDATE affects zero rows. A spike means either a hot event selling out (fine) or an inventory bug (not fine), and distinguishing those quickly is the difference between a calm night and a bad one. Also tracked: `tickets.purchased`, `purchase.idempotent_replays`, and purchase duration by outcome.

Traces cover the purchase flow with a child span around the inventory claim, tagged with tier, requested quantity, and whether the claim succeeded.

OpenTelemetry was chosen for vendor neutrality — the same instrumentation exports to Jaeger locally, Datadog, or Azure Application Insights without code changes.

### Scaling path

| Concern | Approach |
|---|---|
| Availability reads | Read replicas plus short-TTL cache. Reads are stale-tolerant; writes stay authoritative. |
| Hot events | Partition or shard by event; the per-tier row is the contention point. |
| Read/write asymmetry | CQRS-style split — reporting reads never touch the write path. |
| Traffic spikes | Rate limiting per client; queue-based admission control for high-demand on-sales. |

### Reliability

Health checks at `/health/live` and `/health/ready` for orchestration. Circuit breaker on any downstream payment provider. Transactional outbox for publishing `TicketPurchased` events reliably rather than dual-writing.

### Delivery

Migrations gated separately from application deploy so schema and code roll forward independently. Feature flags for progressive rollout of pricing or inventory changes. Alerting on oversell-attempt rate, purchase p99 latency, and drift between `SoldQuantity` and the sum of order lines.

---

## AI Collaboration and Acceleration

AI tooling was used deliberately, with a clear line between where it accelerated the work and where it would have introduced a defect.

### Where AI accelerated the work

| Area | Contribution |
|---|---|
| Project scaffolding | Solution structure, `.csproj` configuration, package references |
| EF Core configuration | `DbContext` fluent mappings, relationship configuration, migration scaffolding |
| DTOs and boilerplate | Request/response records, controller action shells, mapping code |
| Test harness | `WebApplicationFactory` setup, fixture plumbing, arrange-phase seeding helpers |
| Observability wiring | Serilog and OpenTelemetry registration boilerplate |
| Documentation | First-draft structure for this README |

Roughly 60% of the total line count. Every generated file was reviewed line by line before being committed.

### Where I overrode it

Six corrections, listed in order of how much damage each would have caused.

**1. The inventory claim — a lost-update race.**
The first suggested implementation read `SoldQuantity` into memory, compared it in C#, and saved:

```csharp
// generated — WRONG
if (tier.SoldQuantity + qty <= tier.AllocatedQuantity) {
    tier.SoldQuantity += qty;
    await _db.SaveChangesAsync();
}
```

Two concurrent buyers both read 9 remaining, both pass the check, both write. Oversold. This compiles, reads naturally, and passes any test that doesn't run requests in parallel — which is exactly why it's dangerous.

Replaced with a single atomic conditional UPDATE where the database evaluates the condition and performs the claim in one statement. I also added a `CHECK` constraint as a storage-level backstop and deterministic ordering of order lines to prevent deadlocks on multi-tier purchases. None of the three were suggested.

**2. Idempotency that only handled the easy case.**
The generated version looked up the existing key and returned early. That covers a sequential retry but not two concurrent requests carrying the same key — both pass the lookup, both proceed, both create an order. I added the unique index on `IdempotencyKey` and a catch for SQL Server error 2627/2601, so the database arbitrates and the loser replays the winner's result.

**3. A null dereference on the idempotency replay path.**
The generated catch block returned the recovered order without checking it existed. `FirstOrDefaultAsync` can return null when the winning transaction has not yet committed, which would have thrown a `NullReferenceException` inside the handler written to prevent a failure — a 500 in precisely the scenario the code exists to handle. Caught by reading the failure path rather than by a test, and fixed manually with a guard that returns a 409 "please retry".

**4. A stale change-tracker read after a failed save.**
After `SaveChangesAsync` throws, the failed `Order` remains tracked in `Added` state. A tracking-enabled query for the winning order can resolve to that unsaved local entity through EF's identity resolution, returning an order that was never persisted. Fixed by adding `AsNoTracking()` to both idempotency lookups.

**5. A concurrency test that tested nothing.**
The generated test used `foreach` with `await`, which runs sequentially. It passed, and proved nothing about concurrent behaviour. Rewritten with `Task.WhenAll`, and the connection string raised to `Max Pool Size=200` — with the default pool of 100, parallel requests queue inside ADO.NET and the test measures the connection pool rather than the database.

**6. Silent fallthrough on unhandled enum members.**
The generated `switch` on `PurchaseStatus` had a default arm returning 500. A newly added status would have failed quietly in production instead of loudly in testing. Changed to throw.

### The pattern in these

All six were introduced by generated code and corrected by hand. Five are correctness-under-concurrency problems, and every one produces code that looks right in review and passes a single-threaded test. That is the specific weakness: AI optimises for plausible-looking code, and a race condition is defined by being plausible-looking until two requests arrive in the same millisecond.

Notably, four of the six sit on unhappy paths — what happens when the row isn't there, when the save throws, when two requests collide. Those are the paths a green test suite never exercises unless you deliberately write for them.

### How I think about it

AI is fast at structure and reliably naive about correctness under contention. Used well it removes most of the mechanical work; accepted unreviewed on a purchase path it would have shipped an oversell bug, a duplicate-charge bug, a 500 on the recovery path, and a test that proved none of them existed.

So I used it heavily for the 80% that is mechanical, and reviewed line by line on the 20% where being wrong is expensive. That mirrors how I use it day to day: acceleration on scaffolding, boilerplate, and test harnesses; manual verification on concurrency, transaction boundaries, failure paths, and anything touching money.

The discipline that caught all six was reading the unhappy paths specifically — what happens when the row isn't there, when the save throws, when two requests arrive together. Tests confirm the behaviour you thought to write; they don't surface the behaviour you didn't.

---

## What I'd Do Next

1. **Reservation / hold flow** — hold inventory for a short TTL during checkout with a sweeper releasing expiries, so buyers aren't racing during payment entry. This is how production ticketing actually works and is the most significant gap.
2. **Authentication and authorization** — event organisers manage only their own events; purchases tied to authenticated customers.
3. **Payment integration** — with the outbox pattern for reliable state transitions and reconciliation against provider status.
4. **Seat-level inventory** — a materially harder problem than counters: adjacency, holds, and seat maps.
5. **Refunds and cancellations** — inventory return, partial refunds, and the audit trail around both.
6. **Load testing** — validate behaviour at realistic on-sale concurrency, not just the 100-request test.
