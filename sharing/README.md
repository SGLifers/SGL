# SGL sharing service

The service stores Discord profiles, item metadata and immutable revisions in DynamoDB, and content-addressed map/mode files in private S3. Bundles reference exact map and mode revisions. HTTP API and Lambda serve metadata, OAuth callbacks and short-lived S3 URLs. No CloudFront, EC2, database server, VPC or NAT gateway is required.

## Deployment

The requested target region is **us-west-2**. The deployment script requires an explicit account ID and named AWS CLI profile, verifies the caller account before writing, and never reads credentials into app configuration. The target profile is still pending; no infrastructure has been deployed by this implementation.

Requirements: AWS CLI v2, PowerShell, deployment permissions for CloudFormation/IAM/Lambda/API Gateway/S3/DynamoDB/Secrets Manager/CloudWatch Logs (and Budgets if enabled).

```powershell
./sharing/infra/deploy.ps1 -Profile YOUR_PROFILE -AccountId YOUR_ACCOUNT_ID -Region us-west-2 -PlanOnly
./sharing/infra/deploy.ps1 -Profile YOUR_PROFILE -AccountId YOUR_ACCOUNT_ID -Region us-west-2 -BudgetEmail YOU@example.com
```

`-PlanOnly` checks the account/template and builds a local ZIP; it does not create a CloudFormation change set or modify AWS. Deployment uploads the ZIP to a private account/region artifact bucket, creates/updates the `sgl-sharing` stack, records outputs in ignored `artifacts/sharing-deploy/outputs.json`, and writes the public API endpoint to the desktop's embedded `library-service.json`. Rebuild the app after deployment, or enter the endpoint in Settings while testing.

The stack includes a private encrypted/versioned content bucket, on-demand DynamoDB with point-in-time recovery, Python 3.13 arm64 Lambda, a restricted execution role, HTTP API, 14-day logs and a Discord secret. API limits are 10 requests/second with burst 20; Lambda concurrency is 2. Publishing is limited per signed-in account to 100 revisions/day and 20 staged map uploads/day. Map uploads are bounded to 128 MB and signed for one unique key, exact length and SHA-256. Staging files expire after one day. Immutable content is retained and deduplicated by hash. There is no global storage cap; a budget alert is **not a spending cap**. Public download egress and retained content grow with usage.

S3, DynamoDB and the Discord secret have retain policies. Deleting the stack does not erase community content or secrets. The code artifact bucket also remains. Account for retained resources when shutting down or recreating a stack. This version has no automated disaster recovery drill or production service-level guarantee.

## Create the SGL Discord application

1. Sign in to the [Discord Developer Portal](https://discord.com/developers/applications), create an application named **SGL**, and retain it under the community's intended owner/team.
2. In **OAuth2**, add the exact `DiscordCallbackUrl` returned by deployment. A bot or server permissions are not required.
3. Copy the public **Application/Client ID**. Store the client secret directly with the command below; do not paste it into chat, source files or issue reports.

```powershell
./sharing/infra/configure-discord.ps1 -Profile YOUR_PROFILE -Region us-west-2 -ClientId YOUR_CLIENT_ID
```

The script prompts for the secret without echo, writes a temporary file with a user-only ACL, sends it to the stack's Secrets Manager secret, and removes the file. Lambda refreshes its cached secret within 60 seconds. Configuration uses the confidential authorization-code flow with PKCE and only `identify`. A separate PKCE proof ties the desktop's polling request to the browser sign-in; it is single-use. Discord tokens are used transiently to obtain identity and are never persisted. SGL sessions are opaque random tokens, stored hashed in DynamoDB with 30-day expiry and encrypted using DPAPI on Windows.

## Validate before community rollout

```powershell
pip install -r sharing/requirements-dev.txt
python -m unittest discover -s sharing/tests -v
cfn-lint -r us-west-2 -t sharing/infra/template.json
./sharing/tests/test-deploy.ps1
```

Tests use a memory repository and Moto AWS emulation, including DynamoDB transactions, counters, S3 conditional writes, OAuth device proof, ownership/visibility boundaries, revision conflicts, immutable metadata, exact bundle pins and malformed archive rejection. CI runs these checks; it does not deploy automatically.

After deployment, check `/health`, complete a real Discord sign-in, publish a small unlisted mode, install it on a separate test profile, publish a revision, compare/restore it, and test a map bundle. Confirm private items reject anonymous reads and cross-account writes. Verify CloudWatch errors, budget delivery and backup recovery before publishing a stable app release. Local/emulated tests do not establish live AWS or Discord compatibility.

API: `POST /auth/start`, `/auth/poll`, `/auth/logout`; `GET /auth/callback`, `/me`, `/users/{id}`; `GET /items`, `/me/items`, `/items/{id}`, `/items/{id}/revisions`, `/items/{id}/revisions/{revision}`; `POST /uploads`, `/items`, `/items/{id}/revisions`; `GET /share/{id}?revision=...`. Mutation requests require a bearer session; publication requests carry an idempotency ID and updates require the expected latest revision. Signed S3 URLs never receive SGL bearer tokens.

See [player workflow and limitations](../docs/sharing.md).
