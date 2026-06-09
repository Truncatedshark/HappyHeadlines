workspace "HappyHeadlines" "Positive news microservices platform" {

    model {
        user      = person "Reader"    "Reads articles and submits comments."
        publisher = person "Publisher" "Publishes articles via the API."

        system = softwareSystem "HappyHeadlines" "Distributes positive news globally." {

            # ── Infrastructure ──────────────────────────────────────────────────
            nginx = container "nginx" "Round-robin load balancer across 3 ArticleService replicas" "nginx" "Infrastructure"

            # ── Messaging ───────────────────────────────────────────────────────
            rabbitMQ = container "RabbitMQ" "Async message broker. Queues: articles, subscribers" "RabbitMQ 3" "Messaging"

            # ── Caches ──────────────────────────────────────────────────────────
            redisArticles = container "redis_articles" "Article cache. TTL 15 min default. Warmed every 5 min." "Redis 7" "Cache"
            redisComments = container "redis_comments" "Comment cache. TTL 15 min. LRU-30 eviction via sorted set." "Redis 7" "Cache"

            # ── Databases ───────────────────────────────────────────────────────
            dbArticles     = container "Article Databases"  "8 regional PostgreSQL shards: Africa, Antarctica, Asia, Australia, Europe, NorthAmerica, SouthAmerica, Global" "PostgreSQL 16" "Database"
            dbComments     = container "db_comments"    "Stores Comment rows (Pending / Approved / Rejected)" "PostgreSQL 16" "Database"
            dbDrafts       = container "db_drafts"      "Stores Draft rows" "PostgreSQL 16" "Database"
            dbProfanity    = container "db_profanity"   "Stores ProfanityWord rows" "PostgreSQL 16" "Database"
            dbSubscribers  = container "db_subscribers" "Stores Subscriber rows (unique Email index)" "PostgreSQL 16" "Database"

            # ── Feature flags ───────────────────────────────────────────────────
            featureHub = container "FeatureHub" "Feature flag server. Flag: subscriber_service_enabled" "FeatureHub"

            # ── Observability ───────────────────────────────────────────────────
            prometheus = container "Prometheus" "Scrapes /metrics from services"        "Prometheus" "Monitoring"
            grafana    = container "Grafana"    "Cache hit-ratio dashboards (port 3000)" "Grafana"    "Monitoring"
            seq        = container "Seq"        "Structured log aggregation (port 5341)" "Seq"        "Monitoring"
            jaeger     = container "Jaeger"     "Distributed tracing (port 16686)"       "Jaeger"     "Monitoring"

            # ── ArticleService ──────────────────────────────────────────────────
            articleService = container "ArticleService" "Consumes new articles from queue; serves reads with cache-aside. 3 replicas behind nginx." ".NET 8 / ASP.NET Core" {

                articleEndpoints = component "ArticleEndpoints" "Static class. Registers Minimal API routes: POST /articles, GET /articles?region, GET /articles/{id}?region, PUT /articles/{id}?region, DELETE /articles/{id}?region. Uses records CreateArticleRequest and UpdateArticleRequest." "C# / Minimal API"

                articleDbContextFactory = component "ArticleDbContextFactory" "Reads 8 regional connection strings from IConfiguration. CreateForRegion(Region) returns a scoped ArticleDbContext pointed at the correct PostgreSQL shard." "C# / EF Core"

                articleCache = component "ArticleCache" "IConfiguration + ConnectionMultiplexer injected via constructor. List key: articles:{region}. Item key: article:{id}:{region}. Methods: GetArticlesAsync, SetArticlesAsync, GetArticleAsync, SetArticleAsync." "C# / StackExchange.Redis"

                articleQueueConsumer = component "ArticleQueueConsumer" "BackgroundService. Connects to RabbitMQ on startup; declares articles queue. Deserialises ArticleMessage; extracts W3C traceparent for Jaeger. Calls ArticleDbContextFactory.CreateForRegion to persist; calls ArticleCache to invalidate." "C# / RabbitMQ.Client / OpenTelemetry"

                articleCacheWarmer = component "ArticleCacheWarmer" "BackgroundService. On startup and every 5 min calls WarmAllRegionsAsync: iterates all Region values, queries ArticleDbContextFactory, writes to ArticleCache with 10-min TTL." "C# / IHostedService"
            }

            # ── CommentService ──────────────────────────────────────────────────
            commentService = container "CommentService" "Manages reader comments; moderates via ProfanityService with Polly circuit-breaker fault isolation." ".NET 8 / ASP.NET Core" {

                commentEndpoints = component "CommentEndpoints" "Static class. Routes: POST /comments (record CreateCommentRequest), GET /comments?articleId, DELETE /comments/{id}. Returns 202 Accepted when circuit breaker is open (comment saved as Pending)." "C# / Minimal API"

                commentDbContext = component "CommentDbContext" "DbContext with DbSet<Comment>. Comment has: Id (Guid), ArticleId (Guid), Author, Content, Status (CommentStatus enum: Pending/Approved/Rejected), CreatedAt." "C# / EF Core"

                commentCache = component "CommentCache" "Per-articleId cache. Key: comments:{articleId}. LRU tracking via sorted set comments:lru scored by Unix timestamp; evicts oldest entry when count exceeds 30. Methods: GetCommentsAsync, SetCommentsAsync, InvalidateAsync." "C# / StackExchange.Redis"

                profanityClient = component "ProfanityServiceClient" "Typed HttpClient. CheckTextAsync(string text) POSTs to /check and returns CheckResult {bool ContainsProfanity, List<string> MatchedWords}. Polly circuit breaker: 50% failure ratio, min 3 calls, 30s sampling window, 15s break duration." "C# / Polly / HttpClient"

                pendingCommentProcessor = component "PendingCommentProcessor" "BackgroundService. Every 30 s queries CommentDbContext for Status==Pending comments. Calls ProfanityServiceClient.CheckTextAsync; sets status to Approved or Rejected; saves changes." "C# / IHostedService"
            }

            # ── DraftService ────────────────────────────────────────────────────
            draftService = container "DraftService" "Manages publisher article drafts." ".NET 8 / ASP.NET Core" {

                draftEndpoints = component "DraftEndpoints" "Static class. Routes: POST /drafts, GET /drafts, GET /drafts/{id}, PUT /drafts/{id}, DELETE /drafts/{id}. Draft model: Id, Title, Content, Author, CreatedAt, UpdatedAt." "C# / Minimal API"

                draftDbContext = component "DraftDbContext" "DbContext with DbSet<Draft>. Draft entity mapped to drafts table." "C# / EF Core"
            }

            # ── NewsletterService ───────────────────────────────────────────────
            newsletterService = container "NewsletterService" "Sends newsletters to all subscribers; consumes welcome-mail events from RabbitMQ." ".NET 8 / ASP.NET Core" {

                newsletterEndpoints = component "NewsletterEndpoints" "Route: POST /newsletters/send. Calls SubscriberServiceClient.GetSubscribersAsync(); iterates and dispatches newsletter to each. Returns 503 if circuit breaker is open." "C# / Minimal API"

                subscriberClient = component "SubscriberServiceClient" "Typed HttpClient. GetSubscribersAsync() GETs /subscribers on SubscriberService; returns List<SubscriberMessage> {Id, Email, SubscribedAt}. Circuit breaker: 50% failure / 30s window / 15s break." "C# / Polly / HttpClient"

                subscriberQueueConsumer = component "SubscriberQueueConsumer" "BackgroundService. Connects to RabbitMQ; consumes from subscribers queue. Deserialises SubscriberMessage and logs the welcome-mail event." "C# / RabbitMQ.Client"
            }

            # ── ProfanityService ────────────────────────────────────────────────
            profanityService = container "ProfanityService" "Checks submitted text against a managed banned-word list. Called by CommentService." ".NET 8 / ASP.NET Core" {

                profanityEndpoints = component "ProfanityEndpoints" "Routes: POST /check (CheckRequest→CheckResponse {bool ContainsProfanity, List<string> MatchedWords}), GET /words, POST /words (AddWordRequest), DELETE /words/{word}. DB seeded with: badword, spam, hate." "C# / Minimal API"

                profanityDbContext = component "ProfanityDbContext" "DbContext with DbSet<ProfanityWord>. ProfanityWord: int Id, string Word." "C# / EF Core"
            }

            # ── PublisherService ────────────────────────────────────────────────
            publisherService = container "PublisherService" "Accepts article submissions; enqueues asynchronously; returns 202 Accepted immediately." ".NET 8 / ASP.NET Core" {

                publisherEndpoints = component "PublisherEndpoints" "Route: POST /publish (PublishRequest {Title, Content, Author, Region}). Builds ArticleMessage with new Guid. Calls ArticleQueuePublisher.Publish. Returns 202 with generated Id and Region." "C# / Minimal API"

                articleQueuePublisher = component "ArticleQueuePublisher" "Implements IDisposable. Constructor opens RabbitMQ connection and declares articles queue. Publish(ArticleMessage) serialises to JSON, injects W3C traceparent header via ActivitySource for end-to-end Jaeger trace linking." "C# / RabbitMQ.Client / OpenTelemetry"
            }

            # ── SubscriberService ───────────────────────────────────────────────
            subscriberService = container "SubscriberService" "Manages newsletter subscriptions. Feature-flag gated via FeatureHub." ".NET 8 / ASP.NET Core" {

                subscriberEndpoints = component "SubscriberEndpoints" "Routes: POST /subscribers (SubscribeRequest {Email}), DELETE /subscribers/{email}, GET /subscribers. POST adds Subscriber row and enqueues welcome-mail event." "C# / Minimal API"

                subscriberDbContext = component "SubscriberDbContext" "DbContext with DbSet<Subscriber>. Subscriber: Id (Guid), Email (unique index), SubscribedAt." "C# / EF Core"

                subscriberQueuePublisher = component "SubscriberQueuePublisher" "Implements IDisposable. Publish(Subscriber) serialises to JSON and publishes to RabbitMQ subscribers queue, triggering welcome-mail flow in NewsletterService." "C# / RabbitMQ.Client"

                featureHubMiddleware = component "FeatureHub Middleware" "Runs on every request. Reads subscriber_service_enabled flag from FeatureHub SDK. Returns 503 Service Unavailable if flag is off. Fail-open: if FeatureHub is unreachable, proceeds normally." "C# / FeatureHub SDK"
            }
        }

        # ── External relationships ────────────────────────────────────────────
        user      -> nginx          "GET /articles, GET /comments"
        publisher -> publisherService "POST /publish"

        # ── Container relationships ───────────────────────────────────────────
        nginx            -> articleService   "Round-robin HTTP"
        articleService   -> redisArticles    "Cache-aside (StackExchange.Redis)"
        articleService   -> dbArticles       "EF Core (8 regional shards)"
        articleService   -> rabbitMQ         "Consume articles queue"
        publisherService -> rabbitMQ         "Publish to articles queue"
        commentService   -> dbComments       "EF Core"
        commentService   -> redisComments    "Cache-aside (StackExchange.Redis)"
        commentService   -> profanityService "POST /check"
        draftService     -> dbDrafts         "EF Core"
        newsletterService -> subscriberService "GET /subscribers"
        newsletterService -> rabbitMQ        "Consume subscribers queue"
        subscriberService -> dbSubscribers   "EF Core"
        subscriberService -> rabbitMQ        "Publish to subscribers queue"
        subscriberService -> featureHub      "subscriber_service_enabled flag"
        profanityService  -> dbProfanity     "EF Core"
        prometheus -> articleService  "Scrape /metrics"
        prometheus -> commentService  "Scrape /metrics"
        prometheus -> subscriberService "Scrape /metrics"
        grafana    -> prometheus      "PromQL queries"

        # ── ArticleService: component → external ─────────────────────────────
        articleCache          -> redisArticles  "IDatabase.StringGetAsync / StringSetAsync"
        articleDbContextFactory -> dbArticles   "DbContextOptionsBuilder per region"
        articleQueueConsumer  -> rabbitMQ       "IModel.BasicConsume on articles queue"

        # ── ArticleService: component → component ────────────────────────────
        articleEndpoints      -> articleDbContextFactory "CreateForRegion(region)"
        articleEndpoints      -> articleCache            "GetArticlesAsync / SetArticlesAsync / GetArticleAsync"
        articleQueueConsumer  -> articleDbContextFactory "CreateForRegion(msg.Region)"
        articleQueueConsumer  -> articleCache            "SetArticleAsync (invalidate list)"
        articleCacheWarmer    -> articleDbContextFactory "CreateForRegion per Region value"
        articleCacheWarmer    -> articleCache            "SetArticlesAsync(region, list, ttl:10min)"

        # ── CommentService: component → external ─────────────────────────────
        commentDbContext   -> dbComments    "EF Core queries"
        commentCache       -> redisComments "IDatabase operations + sorted set for LRU"
        profanityClient    -> profanityService "POST /check (circuit-breaker)"

        # ── CommentService: component → component ────────────────────────────
        commentEndpoints         -> commentDbContext  "Add / SaveChangesAsync"
        commentEndpoints         -> commentCache      "GetCommentsAsync / InvalidateAsync"
        commentEndpoints         -> profanityClient   "CheckTextAsync(content)"
        pendingCommentProcessor  -> commentDbContext  "Query Pending; update Status; SaveChangesAsync"
        pendingCommentProcessor  -> profanityClient   "CheckTextAsync(content)"

        # ── DraftService: component → external ───────────────────────────────
        draftDbContext -> dbDrafts "EF Core queries"

        # ── DraftService: component → component ──────────────────────────────
        draftEndpoints -> draftDbContext "CRUD operations"

        # ── NewsletterService: component → external ──────────────────────────
        subscriberClient       -> subscriberService "GET /subscribers (circuit-breaker)"
        subscriberQueueConsumer -> rabbitMQ "IModel.BasicConsume on subscribers queue"

        # ── NewsletterService: component → component ─────────────────────────
        newsletterEndpoints    -> subscriberClient "GetSubscribersAsync()"

        # ── ProfanityService: component → external ───────────────────────────
        profanityDbContext -> dbProfanity "EF Core queries"

        # ── ProfanityService: component → component ──────────────────────────
        profanityEndpoints -> profanityDbContext "Query words; case-insensitive text check; add/delete"

        # ── PublisherService: component → external ───────────────────────────
        articleQueuePublisher -> rabbitMQ "IModel.BasicPublish to articles queue"

        # ── PublisherService: component → component ──────────────────────────
        publisherEndpoints -> articleQueuePublisher "Publish(ArticleMessage)"

        # ── SubscriberService: component → external ──────────────────────────
        subscriberDbContext      -> dbSubscribers "EF Core queries"
        subscriberQueuePublisher -> rabbitMQ      "IModel.BasicPublish to subscribers queue"
        featureHubMiddleware     -> featureHub    "GetFlag(subscriber_service_enabled)"

        # ── SubscriberService: component → component ─────────────────────────
        subscriberEndpoints -> subscriberDbContext      "Add / Query / Delete"
        subscriberEndpoints -> subscriberQueuePublisher "Publish(subscriber)"

        # ── Entry-point relationships required by C4 dynamic views ────────────
        publisher    -> publisherEndpoints      "POST /publish"
        nginx        -> articleEndpoints        "Routes HTTP request"
        rabbitMQ     -> articleQueueConsumer    "Delivers ArticleMessage"
        user         -> commentEndpoints        "POST /comments"
        user         -> subscriberEndpoints     "POST /subscribers"
        rabbitMQ     -> subscriberQueueConsumer "Delivers SubscriberMessage"
    }

    views {

        # ── C1 ─ System Context ───────────────────────────────────────────────
        systemContext system "C1_SystemContext" "C1 — System Context: external actors and the HappyHeadlines system" {
            include *
            autoLayout
        }

        # ── C2 ─ Containers ───────────────────────────────────────────────────
        container system "C2_Containers" "C2 — Container: all Docker services and their interactions" {
            include *
            autoLayout
        }

        # ── C3 ─ Components ───────────────────────────────────────────────────
        component articleService "C3_ArticleService" "C3 — ArticleService internal components" {
            include *
            autoLayout
        }

        component commentService "C3_CommentService" "C3 — CommentService internal components" {
            include *
            autoLayout
        }

        component draftService "C3_DraftService" "C3 — DraftService internal components" {
            include *
            autoLayout
        }

        component newsletterService "C3_NewsletterService" "C3 — NewsletterService internal components" {
            include *
            autoLayout
        }

        component profanityService "C3_ProfanityService" "C3 — ProfanityService internal components" {
            include *
            autoLayout
        }

        component publisherService "C3_PublisherService" "C3 — PublisherService internal components" {
            include *
            autoLayout
        }

        component subscriberService "C3_SubscriberService" "C3 — SubscriberService internal components" {
            include *
            autoLayout
        }

        # ── C4 ─ Dynamic flows (code-level interaction sequences) ─────────────
        #
        # Dynamic views must be scoped to a container (not system) to reference
        # components. Cross-service flows are split into one view per container.

        # C4 — Publish article (PublisherService side)
        dynamic publisherService "C4_PublishArticle" "C4 — POST /publish: PublisherService code flow" {
            publisher -> publisherEndpoints "1. POST /publish {title,content,author,region}"
            publisherEndpoints -> articleQueuePublisher "2. Publish(new ArticleMessage{Id=Guid.NewGuid()})"
            articleQueuePublisher -> rabbitMQ "3. IModel.BasicPublish to articles queue + W3C traceparent header"
            autoLayout
        }

        # C4 — Consume article (ArticleService side)
        dynamic articleService "C4_ConsumeArticle" "C4 — ArticleQueue consume: ArticleService code flow" {
            rabbitMQ -> articleQueueConsumer "1. IModel.BasicConsume — receives ArticleMessage"
            articleQueueConsumer -> articleDbContextFactory "2. CreateForRegion(msg.Region)"
            articleDbContextFactory -> dbArticles "3. new ArticleDbContext(connectionString[region])"
            articleQueueConsumer -> articleCache "4. SetArticleAsync(article)"
            articleCache -> redisArticles "5. IDatabase.StringSetAsync"
            autoLayout
        }

        # C4 — Read article with cache-aside (ArticleService side)
        dynamic articleService "C4_ReadArticle" "C4 — GET /articles: cache-aside code flow" {
            nginx -> articleEndpoints "1. GET /articles?region=Europe (round-robin)"
            articleEndpoints -> articleCache "2. GetArticlesAsync(region)"
            articleCache -> redisArticles "3a. StringGetAsync → HIT: return list"
            articleEndpoints -> articleDbContextFactory "3b. CreateForRegion(Europe) — cache MISS only"
            articleDbContextFactory -> dbArticles "4b. SELECT * FROM Articles WHERE Region=Europe"
            articleEndpoints -> articleCache "5b. SetArticlesAsync(Europe, list, ttl:15min)"
            autoLayout
        }

        # C4 — Submit comment with circuit-breaker (CommentService side)
        dynamic commentService "C4_SubmitComment" "C4 — POST /comments: circuit-breaker code flow" {
            user -> commentEndpoints "1. POST /comments {articleId, author, content}"
            commentEndpoints -> profanityClient "2. CheckTextAsync(content)"
            profanityClient -> profanityService "3. POST /check [Polly circuit-breaker]"
            commentEndpoints -> commentDbContext "4. db.Comments.Add({Status=Approved}); SaveChangesAsync()"
            commentEndpoints -> commentCache "5. InvalidateAsync(articleId)"
            autoLayout
        }

        # C4 — Pending comment retry background flow (CommentService side)
        dynamic commentService "C4_PendingCommentRetry" "C4 — PendingCommentProcessor: background recovery flow" {
            pendingCommentProcessor -> commentDbContext "1. Every 30s: WHERE Status = Pending"
            pendingCommentProcessor -> profanityClient "2. CheckTextAsync(comment.Content)"
            profanityClient -> profanityService "3. POST /check — circuit now closed"
            pendingCommentProcessor -> commentDbContext "4. comment.Status = Approved|Rejected; SaveChangesAsync()"
            autoLayout
        }

        # C4 — New subscriber welcome-mail flow (SubscriberService side)
        dynamic subscriberService "C4_NewSubscriber" "C4 — POST /subscribers: welcome-mail code flow" {
            user -> subscriberEndpoints "1. POST /subscribers {email}"
            featureHubMiddleware -> featureHub "2. GetFlag(subscriber_service_enabled) → true"
            subscriberEndpoints -> subscriberDbContext "3. db.Subscribers.Add(new Subscriber{Email}); SaveChangesAsync()"
            subscriberEndpoints -> subscriberQueuePublisher "4. Publish(subscriber)"
            subscriberQueuePublisher -> rabbitMQ "5. IModel.BasicPublish to subscribers queue"
            autoLayout
        }

        # C4 — Welcome-mail consume flow (NewsletterService side)
        dynamic newsletterService "C4_WelcomeMailConsume" "C4 — Subscriber queue consume: NewsletterService code flow" {
            rabbitMQ -> subscriberQueueConsumer "1. IModel.BasicConsume — receives SubscriberMessage"
            autoLayout
        }

        # ── Styles ────────────────────────────────────────────────────────────
        styles {
            element "Person" {
                shape Person
                background #08427B
                color #ffffff
            }
            element "Software System" {
                background #1168BD
                color #ffffff
            }
            element "Container" {
                background #438DD5
                color #ffffff
            }
            element "Component" {
                background #85BBD9
                color #000000
            }
            element "Infrastructure" {
                background #999999
                color #ffffff
            }
            element "Messaging" {
                background #E8825C
                color #ffffff
            }
            element "Cache" {
                shape Cylinder
                background #C7472E
                color #ffffff
            }
            element "Database" {
                shape Cylinder
                background #B3C9E7
                color #000000
            }
            element "Monitoring" {
                background #6CB33E
                color #ffffff
            }
        }

        theme default
    }
}
