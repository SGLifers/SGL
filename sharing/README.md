# SGL sharing service

The service stores Discord profiles, item metadata and immutable revisions in DynamoDB, and content-addressed map/mode files in private S3. Bundles reference exact map and mode revisions. HTTP API and Lambda serve metadata, OAuth callbacks and short-lived S3 URLs. No CloudFront, EC2, database server, VPC or NAT gateway is required.

## Deployment

The requested target region is **us-west-2**. The deployment script requires an explicit account ID and named AWS CLI profile, verifies the caller account before writing, and never reads credentials into app configuration. Deployment defaults to shared, unreserved Lambda concurrency, allowing small new-account quotas.

Requirements: AWS CLI v2, PowerShell, deployment permissions for CloudFormation/IAM/Lambda/API Gateway/S3/DynamoDB/Secrets Manager/CloudWatch/SNS/SQS/EventBridge/Budgets, and a monitored email address. Budget controls and an email recipient are required for deployment.

Reservations are **off by default**. Both functions share the account's unreserved capacity (10 in a new account), so the guard can queue behind other invocations under load. After quota approval, redeploy with `-UseReservedConcurrency` to reserve 2 executions for the API and 1 for the guard. That optional mode requires a regional quota of at least **103**, because AWS requires 100 units to remain unreserved. Its preflight rejects insufficient quotas before creating resources. A quota increase does not provision paid capacity. Other reserved functions can require additional account capacity.

```powershell
./sharing/infra/deploy.ps1 -Profile YOUR_PROFILE -AccountId YOUR_ACCOUNT_ID -Region us-west-2 -BudgetEmail YOU@example.com -PlanOnly
./sharing/infra/deploy.ps1 -Profile YOUR_PROFILE -AccountId YOUR_ACCOUNT_ID -Region us-west-2 -BudgetEmail YOU@example.com -MonthlyBudgetUsd 30
```

`-PlanOnly` checks the account/template and existing budget-control status, then builds a local ZIP. It does not create a CloudFormation change set or change resource configuration; the read-only status invocation can produce normal Lambda logs/usage. Deployment uploads the ZIP to a private account/region artifact bucket, creates/updates the `sgl-sharing` stack, records outputs in ignored `artifacts/sharing-deploy/outputs.json`, and writes the public API endpoint to the desktop's embedded `library-service.json`. It preserves an existing pause in the stack parameters and reconciles the guard after deployment. A failed or malformed guard status stops deployment. Rebuild the app after deployment. Development builds can use `--sharing-service https://your-test-endpoint` to select a test service; release builds always use the embedded endpoint.

The stack includes a private encrypted/versioned content bucket, on-demand DynamoDB with point-in-time recovery, Python 3.13 arm64 Lambda, restricted execution roles, HTTP API, 14-day logs and a Discord secret. API throttling is 10 requests/second with burst 20; Lambda capacity is shared unless optional reservations are enabled. Publishing is limited per signed-in account to 100 revisions/day and 20 staged map uploads/day. Map uploads are bounded to 128 MB and signed for one unique key, exact length and SHA-256. Staging files expire after one day. Immutable content is retained and deduplicated by hash. There is no global storage cap. Public download egress and retained content grow with usage.

## Budget alerts and automatic cutoff

The default **$30 USD monthly budget is account-wide**, including other workloads and regions in this account. Override the amount with `-MonthlyBudgetUsd`. It emails the required `-BudgetEmail` recipient when AWS reports actual cost above 50%, 80% and 100% ($15, $24 and $30 at the default). These use `GREATER_THAN` thresholds, so exact equality does not trigger. Confirm any budget recipient verification email and the separate **SGL budget guard failures** SNS subscription before relying on delivery.

The 100% notification also goes to a dedicated SNS topic. Its scoped policy accepts only this account's SGL budget; the cutoff function accepts only that topic and does not parse AWS's notification prose. It persists a pause under `CONTROL#BUDGET` in DynamoDB, adds a bucket-policy deny for object downloads/uploads (including outstanding presigned URLs), and changes the sharing API route to `AWS_IAM`, blocking ordinary client requests before Lambda runs. Authorized AWS IAM operators can still invoke the API; clients use the separate SGL session and have no AWS credentials. In reserved mode it also sets API reserved concurrency to zero. Both cutoff operations are attempted independently; failures raise an error. No maps, modes, history or user data are deleted. Local editing and portable packages continue to work.

A conditional DynamoDB lease under `CONTROL#BUDGET_LOCK` serializes state changes in both modes. Its 150-second expiry exceeds the function’s 60-second timeout; failed holders can be reclaimed, and only the owning token can release the lease. Read-only status remains available while mutations are locked. A five-minute reconciliation repairs paused controls if they drift; it never automatically resumes sharing, including at the start of a new month. AWS async retries and a 14-day encrypted failure queue retain failed events. Six CloudWatch alarms cover guard errors, dropped events, failed failure-destination delivery, SNS delivery failures, queued failures and scheduler invocation failures. A separate alarm topic emails the monitored recipient. The guard's permissions target only this stack's bucket policy, API route/concurrency, failure queue and dedicated control/lease records; the app role cannot edit that record. Monitoring, storage and guard resources have their own ongoing costs.

Inspect or pause through the operator script, using an authorized AWS profile:

```powershell
./sharing/infra/budget-control.ps1 -Profile YOUR_PROFILE -AccountId YOUR_ACCOUNT_ID -Action Status
./sharing/infra/budget-control.ps1 -Profile YOUR_PROFILE -AccountId YOUR_ACCOUNT_ID -Action Pause
```

After reviewing spend and the cause of the cutoff, explicitly resume:

```powershell
./sharing/infra/budget-control.ps1 -Profile YOUR_PROFILE -AccountId YOUR_ACCOUNT_ID -Action Resume -AcknowledgeBudgetOverrun
```

The script defaults to `us-west-2`; it verifies the account before invoking controls. Resume removes only SGL's budget deny, preserves unrelated bucket rules, restores ordinary route access (and API concurrency to 2 only in reserved mode), then clears the persisted pause. A failed resume attempts to pause again. Duplicate delivery of the same processed budget event does not undo a successful operator resume. Resuming while still above budget permits more spending, and AWS may not send another alert for that same breach.

This is **not an exact dollar cap**: [AWS billing notifications can lag](https://docs.aws.amazon.com/cost-management/latest/userguide/budgets-managing-costs.html), policy changes take time to propagate, and transfers or invocations already in progress can finish. Storage, backups, monitoring, API Gateway/rejected requests and unrelated AWS workloads can continue costing money while sharing is paused. The cutoff does not shut down the AWS account. Changing the budget or redeploying does not itself clear a pause; use the operator script. Administrative edits outside this workflow can bypass controls.

S3, DynamoDB and the Discord secret have retain policies. Deleting the stack does not erase community content or secrets. The code artifact bucket also remains. Account for retained resources when shutting down or recreating a stack. This version has no automated disaster recovery drill or production service-level guarantee.

## Create the SGL Discord application

1. Sign in to the [Discord Developer Portal](https://discord.com/developers/applications), create an application named **SGL**, and retain it under the community's intended owner/team.
2. In **OAuth2**, add the exact `DiscordCallbackUrl` returned by deployment. A bot or server permissions are not required.
3. Copy the public **Application/Client ID**. Store the client secret directly with the command below; do not paste it into chat, source files or issue reports.

```powershell
./sharing/infra/configure-discord.ps1 -Profile YOUR_PROFILE -Region us-west-2 -ClientId YOUR_CLIENT_ID
```

The script prompts for the secret without echo and trims surrounding whitespace. It verifies application authentication against Discord using a deliberately invalid authorization code that cannot create a token. Rejected credentials are not stored. It then writes a temporary file with a user-only ACL, sends it to the stack's Secrets Manager secret, and removes the file. Lambda refreshes its cached secret within 60 seconds. Configuration uses the confidential authorization-code flow with PKCE and only `identify`. A separate PKCE proof ties the desktop's polling request to the browser sign-in; it is single-use. Discord tokens are used transiently to obtain identity and are never persisted. SGL sessions are opaque random tokens, stored hashed in DynamoDB with 30-day expiry and encrypted using DPAPI on Windows.

If Discord sign-in fails, start a fresh attempt from SGL rather than refreshing an old callback URL. A failed callback now ends desktop polling and discards the server-side Discord verifier. CloudWatch records only the failed phase, HTTP status, allowlisted OAuth error identifier and a fixed diagnostic hint; it excludes raw response descriptions, authorization codes, tokens and secrets. `invalid_client` means the administrator should check the current OAuth2 Client Secret for the same application ID; the Public Key and bot token are separate credentials. Rerun the configuration script after changing the secret.

## Validate before community rollout

```powershell
pip install -r sharing/requirements-dev.txt
python -m unittest discover -s sharing/tests -v
cfn-lint -r us-west-2 -t sharing/infra/template.json
./sharing/tests/test-deploy.ps1
```

Tests use memory controls and Moto AWS emulation, including DynamoDB transactions, counters, S3 conditional writes, OAuth device proof, ownership/visibility boundaries, revision conflicts, immutable metadata, exact bundle pins and malformed archive rejection. Guard tests cover persistent pause, partial failure, duplicate notifications, rollback after failed resume, unchanged unrelated bucket policies and real SDK serialization, route changes, lease contention and concurrency calls in both modes. PowerShell tests mock the CLI and check that deployment preserves pauses and rejects failed control status. CI runs these checks; it does not deploy automatically.

After deployment, check `/health`, complete a real Discord sign-in, publish a small unlisted mode, install it on a separate test profile, publish a revision, compare/restore it, and test a map bundle. Confirm private items reject anonymous reads and cross-account writes. Verify budget email delivery and the confirmed monitoring subscription. Generate test upload/download URLs, manually pause, verify new API requests and those old URLs fail, then inspect status and explicitly resume. Check failure alarms, the queue and backup recovery before publishing a stable app release. Also validate an actual SNS cutoff event on the deployed guard. Local/emulated tests do not establish live AWS permissions, SNS delivery or Discord compatibility. A named AWS profile and monitored alert email are required for each deployment.

Run `python sharing/tests/smoke-deployed.py --profile YOUR_PROFILE --account-id YOUR_ACCOUNT_ID` for read-only public endpoint/status checks. Before hosts use the service, add `--exercise-cutoff` to test manual and actual SNS-triggered pauses, previously issued S3 GET/PUT URLs and explicit restoration. This briefly interrupts sharing and creates only uniquely named synthetic staging files, which it removes afterward. It refuses to reopen an existing pause or override an unrelated budget event. Signed URLs and credentials are never printed. S3 presigning explicitly uses regional virtual-host endpoints to avoid global-endpoint signature/region errors on new buckets.

API: `POST /auth/start`, `/auth/poll`, `/auth/logout`; `GET /auth/callback`, `/me`, `/users/{id}`; `GET /items`, `/me/items`, `/items/{id}`, `/items/{id}/revisions`, `/items/{id}/revisions/{revision}`; `POST /uploads`, `/items`, `/items/{id}/revisions`; `GET /share/{id}?revision=...`. Mutation requests require a bearer session; publication requests carry an idempotency ID and updates require the expected latest revision. Signed S3 URLs never receive SGL bearer tokens.

See [player workflow and limitations](../docs/sharing.md).

Map and bundle publications accept an optional `tags` array (20 labels, 40 UTF-16 code units each). Tags are normalized, deduplicated and recorded in immutable revision metadata. Missing tags in updates preserve existing labels; an explicit empty array clears them. `GET /items` and `/me/items` accept case-insensitive `tag` (exact) and `creator` (partial publisher name, Discord ID or SGL user ID) filters alongside existing pagination. Filtering rechecks current visibility before returning metadata. Map owners may submit `content: {"reuseRevisionId": "..."}` on an update to reuse a revision belonging to that same item; this avoids another map upload for tag-only edits. `/health` advertises `mapTags`, `creatorFilter` and `reuseMapRevision` capabilities. No new AWS resources or indexes are required.

Shared item listings and item details include an optional `preview` descriptor with a short-lived S3 URL, SHA-256 and size. Revision descriptors include a preview for that exact revision; bundles resolve their pinned map after checking access. Expiring image URLs are kept outside the item attribution used for portable packages. The service advertises `mapPreviews`; existing screenshot blobs are reused and no additional infrastructure is needed.

### Desktop compatibility

Current desktop builds no longer create, copy or open content share links or register the `sgl:` protocol. Publishing is managed through My uploads, with portable packages for file sharing. The service retains its legacy share-page routes and `shareUrl` response fields for previously released clients; this desktop change does not invalidate their existing URLs or require a backend deployment. New desktop uploads offer Public and Private visibility, while existing unlisted items remain accessible to their owners.
