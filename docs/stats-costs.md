# SGL statistics AWS cost projections

For a small community deployment, budget approximately **$3–7/month for DynamoDB**, with a practical initial budget target of **$10/month**. DynamoDB is now the preferred design: alias changes are rare, public match lists contain only the latest ten per player or gamemode, and totals are recalculated during indexing. PostgreSQL remains a comparison alternative at **$16–20/month** for its smallest configuration or **$28–32/month** with more memory.

These are projected incremental costs for the statistics service and dashboard, not a quote or the existing AWS account's total bill. Rates were checked against AWS's public US West (Oregon), `us-west-2`, price lists on 2026-11-26. Estimates use USD, 730 running hours/month, on-demand pricing, no tax, no reserved commitments, and no free-tier allowances or promotional credits. Eligible allowances can reduce the actual bill. Development labor is excluded.

## Account budget plan

Plan to raise the existing **account-wide budget to $30/month** for the statistics rollout. The deployment script and infrastructure template now default to $30; the live AWS value has not been checked or changed. The statistics service's $10 planning allowance is part of the $30 total, not an additional account budget. Existing services and other account charges share the remaining headroom; their actual costs must be checked before rollout.

Retain actual-cost alerts at 50%, 80% and 100%, which become $15, $24 and $30. The existing greater-than comparison triggers after AWS reports costs above those values. Preserve automatic pause at the final notification and explicit operator resume, extending the current sharing-only guard to the new statistics resources before launch. Notifications can lag and some costs continue after a pause, so $30 is not a guaranteed hard spending ceiling. See [the budget rollout plan](stats-design.md#account-budget-plan). Source defaults are now $30; no live budget was changed.

## Workload assumptions

- 20 matches/day across the whole community, or 600 matches in a 30-day month; one collector submits each match.
- 12 participants and 100 medal events per match: 7,200 participant-match records and 60,000 medal events/month.
- 10,000 dashboard visits/month with ten origin API requests per visit: 100,000 API requests. CDN caching can reduce origin requests.
- Approximately 1 GB of retained DynamoDB data including indexes, plus 1 GB of S3 archive content, 1 GB/month of application logs and 5 GB/month of dashboard transfer. These are sizing allowances, not measured event sizes. Retention increases stored data over time.
- DynamoDB budget: one million write request units and one million read request units/month, including indexes, transactions, retries and derived views. This requires coalesced/targeted aggregate work, not rebuilding every historical record on each request.
- Public reads fetch materialized totals/leaderboards or at most ten recent matches for a player/gamemode, with no public historical-match pagination. Retain older observations and contributions privately for full-history totals and rare repairs. The ten-match cap bounds public reads but does not cap private storage or repair costs. Indexer lookups for recent-list refill, and immutable leaderboard snapshot storage/requests, belong in the same budget.
- SplitStatsInfra will house the backend, API contracts and AWS infrastructure; SplitStatsWeb remains frontend-only. Separate Git repositories do not add an AWS runtime charge. The existing six-match tournament can remain a static curated dataset initially, included in the existing archive/delivery allowances rather than requiring another database or service stack. A later import uses the same statistics service with separate event scope. GitHub Pages currently hosts that archive; S3/CloudFront is the proposed unified hosting destination, not a requirement to pay for duplicate permanent websites. GitHub plan/Actions/storage charges, if any, are separate from this AWS estimate.
- PostgreSQL budget: 20 GB provisioned gp3 storage, a supported PostgreSQL version, bounded Lambda connections, and no NAT gateway, RDS Proxy, paid interface endpoints or public database address.
- Common costs include basic monitoring, queues, lightweight API/worker execution, archives, public hosting and a small secrets allowance. Existing sharing workloads, game hosting, OCR compute, screenshots, WAF, domain registration, paid AWS support and disaster-recovery replicas are outside this estimate.

One host per match simplifies collection; it does not directly set the price. Matches/day, dashboard traffic, retained volume and aggregate/index work determine usage.

## Database rates and arithmetic

### DynamoDB Standard on demand

The official [Oregon DynamoDB price list](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonDynamoDB/current/us-west-2/index.json), published 2026-11-25T15:02:09Z, supplies these rates:

| Component | Rate | Modeled quantity | Monthly cost |
| --- | --- | --- | --- |
| Writes | $0.625 per million write request units | 1 million | $0.625 |
| Reads | $0.125 per million read request units | 1 million | $0.125 |
| Standard storage | $0.25/GB-month, before any free allowance | 1 GB | $0.25 |
| Point-in-time recovery | $0.20/GB-month of protected table data | 1 GB | $0.20 |
| **Database subtotal** | | | **$1.20** |

A request unit is not an API call or a match. Item sizes, read consistency, transactions and indexes affect the number of units billed. A nontransactional write up to 1 KB consumes one write request unit; transactions use more, and index writes are additional. Price the full table/index workload. PITR uses protected table size rather than simply charging every GSI byte again; using the entire modeled 1 GB as protected data is a conservative simplification.

AWS includes an allowance for the first 25 GB of Standard storage, so $0.25 here may not be billed if that account/region allowance remains available. Read/write unit charges in on-demand mode still apply. See [DynamoDB pricing](https://aws.amazon.com/dynamodb/pricing/).

The earlier design's deliberately broad rebuild example generated 2.4 million aggregate-row writes/month before indexes and transaction multipliers. If total billed writes instead reach five million units, the database subtotal becomes $3.70 with the other quantities unchanged. At ten million writes it is $6.83. Those cases would move the service toward roughly $6–12/month; they are reasons to constrain rebuild work, not reasons to abandon DynamoDB.

### RDS PostgreSQL

The official [Oregon RDS price list](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonRDS/current/us-west-2/index.json), published 2026-11-26T15:30:47Z, supplies these rates:

| Configuration | Compute calculation | Storage calculation | Database subtotal/month |
| --- | --- | --- | --- |
| `db.t4g.micro`, Single-AZ, 1 GiB RAM | $0.016/hour × 730 = $11.68 | 20 GB × $0.115 = $2.30 | **$13.98** |
| `db.t4g.small`, Single-AZ, 2 GiB RAM | $0.032/hour × 730 = $23.36 | 20 GB × $0.115 = $2.30 | **$25.66** |
| `db.t4g.small`, Multi-AZ with one standby | $0.065/hour × 730 = $47.45 | 20 GB × $0.23 = $4.60 | **$52.05** |

The micro is a cost floor, not a load-tested recommendation. Memory-intensive joins, IAM database authentication and connection concurrency can make a small instance preferable. Burstable CPU surplus credits, additional backup retention, larger storage or an unsupported engine version's Extended Support fees can increase the bill. Stay on a supported version and bound query/connection work.

The estimate assumes automated backup storage remains within RDS's included active-database backup allowance; extra retained/manual snapshots cost more. Single-AZ has backups but does not provide a standby for automatic availability-zone failover. DynamoDB's regional availability and a Single-AZ RDS instance are therefore not equivalent availability configurations. See [RDS PostgreSQL pricing](https://aws.amazon.com/rds/postgresql/pricing/).

## Common service allowance

These components are needed with either database. They are rounded planning allowances, not individually metered forecasts:

| Component | Monthly allowance under the stated traffic |
| --- | --- |
| HTTP API Gateway | About $0.10 for 100,000 small requests at $1/million |
| Lambda API handlers and bounded background workers | About $0.10–0.50 |
| S3 storage/requests, SQS and receipt metadata | About $0.10–0.50 |
| Public dashboard delivery | About $0.50–1.00, before applicable delivery allowances |
| Basic logs, alarms, secrets and limited custom metrics | About $1–3 |
| **Rounded common service budget** | **$2–5/month** |

The common budget assumes bounded logging and a small number of alarms/custom metrics. It does not include indefinite full request logging, frequent full-history analytics, large exports or unbounded polling. Shared existing secrets can reduce the incremental cost. Relevant primary pricing references are [API Gateway](https://aws.amazon.com/api-gateway/pricing/), [Lambda](https://aws.amazon.com/lambda/pricing/), [S3](https://aws.amazon.com/s3/pricing/), [SQS](https://aws.amazon.com/sqs/pricing/), [CloudFront](https://aws.amazon.com/cloudfront/pricing/), [CloudWatch](https://aws.amazon.com/cloudwatch/pricing/) and [Secrets Manager](https://aws.amazon.com/secrets-manager/pricing/).

## Combined projection

| Option | Database/month | Common services/month | Rounded total/month | Rounded total/year |
| --- | --- | --- | --- | --- |
| DynamoDB, efficient read models | $1.20 | $2–5 | **$3–7** | **$36–84** |
| RDS PostgreSQL micro, Single-AZ | $13.98 | $2–5 | **$16–20** | **$192–240** |
| RDS PostgreSQL small, Single-AZ | $25.66 | $2–5 | **$28–32** | **$336–384** |
| RDS PostgreSQL small, Multi-AZ | $52.05 | $2–5 | **$54–58** | **$648–696** |

At this workload, PostgreSQL's flexibility costs roughly $15–30/month more than an efficient DynamoDB implementation, depending on instance size and actual request usage. That recurring AWS difference can be smaller than the engineering time needed to maintain DynamoDB projections, identity backfills and new filters. No development-hours estimate has been made.

At ten times the DynamoDB request/storage allowance—ten million write units, ten million read units and 10 GB of data/PITR—the database model is $12/month before storage allowances. With a rough $5–15 common service allowance for the larger traffic, plan approximately $17–30/month. This is a sensitivity scenario, not a claim that ten times as many matches necessarily causes ten times every charge. RDS compute does not rise per request, but retaining the same instance at higher usage requires load testing; storage stays $2.30 while the provisioned 20 GB remains sufficient.

## Infrastructure choices that change the result

| Optional component | Approximate added monthly baseline |
| --- | --- |
| One NAT gateway and one charged public IPv4 address | $0.045 × 730 + $0.005 × 730 = **$36.50**, plus processing/transfer |
| RDS Proxy for a 2-vCPU instance | $0.015 × 2 × 730 = **$21.90** |
| One interface endpoint service in two AZs, at $0.01/endpoint-hour | **$14.60**, plus data processing; service/region rates must be checked |
| Custom domain, WAF, extra monitoring or customer-managed encryption keys | Separate costs; not required by the baseline model |

The private PostgreSQL baseline avoids these fixed networking/proxy costs by separating internet-facing Discord/authentication work from database functions. Database Lambdas connect privately to RDS, use IAM database authentication and use free S3/DynamoDB gateway endpoints for archive/receipt operations. SQS-triggered Lambda polling is managed by the Lambda service; the function itself need not call the SQS API. Any new outbound service calls require a fresh networking/cost review. This design does not make the database public to save money.

Use small bounded Lambda connection pools rather than adding RDS Proxy automatically. If a Secrets Manager interface endpoint is needed instead of IAM database authentication, add its price explicitly. Sources: [VPC pricing](https://aws.amazon.com/vpc/pricing/), [RDS Proxy pricing](https://aws.amazon.com/rds/proxy/pricing/), [AWS database proxy comparison](https://aws.amazon.com/blogs/database/understanding-the-differences-between-pgbouncer-and-amazon-rds-proxy-for-postgresql-connection-pooling/) and [PrivateLink pricing](https://aws.amazon.com/privatelink/pricing/).

Aurora PostgreSQL Serverless v2 is a separate option, not the low-cost default. Oregon Standard compute is $0.12/ACU-hour: one instance averaging 0.5 ACU continuously costs **$43.80/month in compute alone**, plus storage and I/O. Supported versions can auto-pause at zero ACUs, reducing compute during genuine inactivity, but persistent connections or public dashboard traffic can prevent meaningful idle periods. Resume adds latency. See [Aurora auto-pause behavior](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/aurora-serverless-v2-auto-pause.html).

## Decision for SGL

Use DynamoDB as the preferred baseline under the updated constraints: rare alias changes, a maximum of ten public recent matches per player/gamemode and indexing-time recalculation. These fixed access patterns support the existing $3–7/month projection and $10/month planning target; they are not grounds to promise a lower bill without measuring indexer work. PostgreSQL's additional flexibility is not required by the current public API.

Keep sanitized observations and current contributions privately beyond the ten displayed matches, with immutable source archives in S3. That preserves correct totals, alias reindexing and a migration/rebuild path without implementing two databases now. SplitgateHost remains the accepted collection reference; the unavailable repositories do not block this decision. No infrastructure has been deployed or billed by this cost exercise.
