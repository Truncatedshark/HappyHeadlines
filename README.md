# HappyHeadlines

A microservices-based positive news platform built with .NET 8, PostgreSQL, RabbitMQ, Redis, and Docker.

## Running the project

```bash
docker compose down && docker compose build --no-cache && docker compose up
```

Use `--no-cache` when pulling in new code changes to ensure Docker rebuilds all images from scratch. For subsequent runs where nothing has changed, `docker compose up` is sufficient.

## Observability

| Tool | URL | Purpose |
|------|-----|---------|
| Seq | http://localhost:5341 | Centralised structured logs from all services |
| Jaeger | http://localhost:16686 | Distributed traces across service boundaries |
| RabbitMQ | http://localhost:15672 | Message queue monitor (guest/guest) |
| Prometheus | http://localhost:9090 | Metrics scraping and querying |
| Grafana | http://localhost:3000 | Cache hit ratio dashboards (no login required) |

**Log retention:** Configure in Seq under Settings → Retention Policies.

**Trace retention:** Jaeger holds up to 10 000 traces in memory, auto-evicting the oldest.

**Log/trace correlation:** Every log entry carries a `TraceId` field. Copy it from Seq and search in Jaeger to jump directly to the corresponding trace.

## Architecture

```
                        ┌─────────────────────────────────────────┐
                        │            nginx (port 80)              │
                        │         Round-robin load balancer       │
                        └──────────┬──────────┬───────────────────┘
                                   │          │
                    ┌──────────────▼──┐   ┌───▼─────────────┐
                    │ ArticleService  │   │ ArticleService  │  ... (3 instances)
                    │   (port 8080)   │   │   (port 8080)   │
                    └──────┬──────────┘   └────────┬────────┘
                           │    ▲                  │    ▲
                           │    │ consume           │    │ consume
                           │  ──┴──────────────────┴──  │
                           │  │       ArticleQueue      │ │
                           │  │       (RabbitMQ)        │ │
                           │  ──────────────────────────  │
                           │              ▲               │
                           │              │ publish       │
                           │    ┌─────────┴──────────┐   │
                           │    │  PublisherService  │   │
                           │    │    (port 8084)     │   │
                           │    └────────────────────┘   │
                           │                             │
              ┌────────────▼─────────────────────────────▼──────┐
              │         ArticleDatabase (8 regional shards)      │
              │  Africa · Antarctica · Asia · Australia ·        │
              │  Europe · NorthAmerica · SouthAmerica · Global   │
              └──────────────────────────────────────────────────┘
                           │                             │
                    ┌──────▼──────┐               ┌──────▼──────┐
                    │redis_articles│              │redis_articles│  (shared)
                    └─────────────┘               └─────────────┘

┌──────────────────────┐        ┌──────────────────────────┐
│    CommentService    │──HTTP──▶   ProfanityService       │
│      (port 8081)     │        │      (port 8082)         │
└──────────┬───────────┘        └────────────┬─────────────┘
           │                                 │
    ┌──────▼──────┐                  ┌───────▼───────┐
    │ db_comments │                  │  db_profanity │
    └─────────────┘                  └───────────────┘
           │
    ┌──────▼──────────┐
    │  redis_comments  │
    └──────────────────┘

┌──────────────────────┐
│    DraftService      │
│      (port 8083)     │
└──────────┬───────────┘
           │
    ┌──────▼──────┐
    │  db_drafts  │
    └─────────────┘
```

## Publish flow

```
POST /publish → PublisherService → ArticleQueue (RabbitMQ) → ArticleService → ArticleDatabase
```

PublisherService injects a W3C `traceparent` header into each RabbitMQ message. ArticleService extracts it on consume, linking the two spans into one unbroken trace in Jaeger.

## Caching

Both caches use a **cache-aside** pattern: the service checks Redis first, and only queries the database on a miss. The result is then stored in Redis for subsequent requests.

### ArticleCache (`redis_articles`)

| Setting | Value |
|---------|-------|
| TTL | 15 minutes (default), 10 minutes (warmer) |
| Warm interval | Every 5 minutes |
| Warm window | Articles published in the last 14 days |

A background service (`ArticleCacheWarmer`) pre-loads all regional article lists into Redis on startup and refreshes them every 5 minutes. This ensures the cache is hot before the first request arrives and never goes fully cold between warm cycles.

Cache is also updated immediately on `POST` and `PUT`, and remains populated on `DELETE`.

### CommentCache (`redis_comments`)

| Setting | Value |
|---------|-------|
| TTL | 15 minutes |
| Max cached article lists | 30 (LRU eviction) |

Caches the approved comment list per article. An **LRU eviction** policy using a Redis sorted set (`comments:lru`) keeps at most 30 article comment lists in memory at any time. When the 31st article is accessed, the least-recently-used entry is evicted. The cache is invalidated on `POST` (new approved comment) and `DELETE`.

### Grafana dashboard

The **HappyHeadlines Cache** dashboard at http://localhost:3000 shows:

| Panel | Description |
|-------|-------------|
| Article Cache Hit Ratio | % of article reads served from Redis (stat, colour-coded) |
| Comment Cache Hit Ratio | % of comment reads served from Redis (stat, colour-coded) |
| Article Cache — Hits vs Misses | Hits/s and misses/s over time (timeseries) |
| Comment Cache — Hits vs Misses | Hits/s and misses/s over time (timeseries) |

Thresholds: red < 50%, yellow 50–80%, green ≥ 80%.

## Services

### PublisherService
Receives publish requests and puts articles onto the ArticleQueue.

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/publish` | Publish an article |

Returns `202 Accepted` with the generated article ID and region. The article is not yet persisted at this point — ArticleService handles that asynchronously.

---

### ArticleService
Consumes articles from the ArticleQueue and stores them in the correct regional database. Also serves read requests. Three instances run behind nginx (X-axis scaling), each with a background consumer sharing the queue load.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/articles?region={region}` | List articles for a region |
| GET | `/articles/{id}?region={region}` | Get a single article |
| PUT | `/articles/{id}?region={region}` | Update an article |
| DELETE | `/articles/{id}?region={region}` | Delete an article |

Available regions: `Africa`, `Antarctica`, `Asia`, `Australia`, `Europe`, `NorthAmerica`, `SouthAmerica`, `Global`

Regional sharding (Z-axis scaling): each region has its own dedicated PostgreSQL database.

GET requests use cache-aside (Redis → DB on miss). POST and PUT write to DB and update the cache immediately. Metrics exposed at `/metrics`.

---

### DraftService
Manages article drafts for publishers.

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/drafts` | Save a new draft |
| GET | `/drafts` | List all drafts |
| GET | `/drafts/{id}` | Get a single draft |
| PUT | `/drafts/{id}` | Update a draft |
| DELETE | `/drafts/{id}` | Delete a draft |

---

### CommentService
Handles reader comments on articles. Calls ProfanityService to moderate content before storing.

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/comments` | Submit a comment |
| GET | `/comments?articleId={guid}` | Get approved comments for an article |
| DELETE | `/comments/{id}` | Delete a comment |

**Comment statuses:**
- `Approved` — passed profanity check, visible to readers
- `Rejected` — contained profanity, discarded
- `Pending` — ProfanityService was unavailable, queued for retry

**Fault isolation — circuit breaker + swimlane pattern:**

CommentService is fault-isolated from ProfanityService using a circuit breaker (Polly):

| Setting | Value |
|---------|-------|
| Failure ratio to open circuit | 50% |
| Minimum calls before tripping | 3 |
| Sampling window | 30 seconds |
| Break duration | 15 seconds |

When the circuit is open, comments are saved with `Pending` status and the endpoint returns `202 Accepted` instead of failing. A background job (`PendingCommentProcessor`) retries all pending comments every 30 seconds. Once ProfanityService recovers, pending comments are resolved to `Approved` or `Rejected` automatically.

GET requests use cache-aside (Redis → DB on miss). Cache is invalidated on POST and DELETE. Metrics exposed at `/metrics`.

---

### ProfanityService
Maintains a list of banned words and checks text against them.

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/check` | Check text for profanity |
| GET | `/words` | List all banned words |
| POST | `/words` | Add a banned word |
| DELETE | `/words/{word}` | Remove a banned word |

Default banned words on first startup: `badword`, `spam`, `hate`

## Tech stack

| Layer | Technology |
|-------|-----------|
| Services | .NET 8 (C#, Minimal APIs) |
| ORM | Entity Framework Core 8 |
| Databases | PostgreSQL 16 |
| Message broker | RabbitMQ 3 |
| Caching | Redis 7 (StackExchange.Redis) |
| Logging | Serilog → Seq |
| Tracing | OpenTelemetry → Jaeger |
| Metrics | prometheus-net → Prometheus → Grafana |
| Resilience | Polly (Microsoft.Extensions.Http.Resilience) |
| Load balancer | nginx |
| Containerisation | Docker Compose |
