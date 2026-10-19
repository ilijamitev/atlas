# Atlas onboarding

Backend for Atlas, digital customer onboarding for a banking group in six markets (MA–MF). A customer sends
their details, an ID photo and a selfie from the app; the backend verifies the identity with IDNow, screens
the name against sanctions and PEP lists with World-Check, opens the account and orders the card.

The requirements contradict each other on purpose: the ticket wants one fully automated call in under three
minutes, and Compliance wants a human to review every possible sanctions match, a branch visit in one market,
and personal data kept in its own country. What I built keeps the ticket's single call for the automated
majority and adds what Compliance forces. The calls I made, and the alternatives I rejected, are in
[docs/reasoning.md](docs/reasoning.md); what only someone else can answer is in
[docs/open-questions.md](docs/open-questions.md). [WHERE-TO-LOOK.md](WHERE-TO-LOOK.md) is the half-page
version.

## Run it

You need Docker and the .NET 10 SDK.

```
powershell -ExecutionPolicy Bypass -File scripts\run.ps1    # Windows
bash scripts/run.sh                                         # macOS, Linux
```

The script starts SQL Server, Seq and Azurite from [`platform/docker-compose.yml`](platform/docker-compose.yml),
builds once, creates and migrates the market databases and containers, and starts the three services:

| Service | URL | What it is |
|---|---|---|
| Onboarding | http://localhost:5100/swagger | The customer journey and the branch-staff activation |
| Screening | http://localhost:5200/swagger | World-Check screening and the compliance officers' cases |
| Simulators | http://localhost:5300/swagger | Stand-ins for IDNow, World-Check and core banking, plus development tokens |
| Seq | http://localhost:5341 | Logs from all three |

Ctrl+C stops the services; the containers keep running (`docker compose -f platform/docker-compose.yml down`
stops them). If a port is taken, set `SQL_PORT`, `SEQ_PORT` or `BLOB_PORT` before running: the containers and
the services both read them. The first application after a start takes a few seconds longer while the runtime
warms up.

**Tests:** `dotnet test src/Atlas.slnx`. The unit tests need nothing. The integration tests need the compose
stack: they run all three services in one process against it, under their own database and container names,
and remove them afterwards. The 50-application load test is tagged `Category=Load` (`--filter Category!=Load`
skips it).

**Layout:** everything the solution owns is in [`src/`](src/): `Atlas.slnx`, the props files and the six
projects. Around it: `platform/` (compose), `scripts/`, `requests/`, `docs/` and the CI workflow.

## Try it

The scenarios in [`requests/`](requests/) run top to bottom in VS Code (REST Client), Rider or Visual Studio,
in the order they're numbered: the happy path, a possible match and the officer's decision, a provider outage,
idempotency, MD's branch activation, save and resume, retakes, erasure, the bounded wait, and access control.
Every applicant in them is made up.

Three things make the simulators drivable without touching code:

| What | How |
|---|---|
| IDNow's answer | A marker in the image bytes: `INVALID`, `INCONCLUSIVE`, `FACE_MISMATCH` or `LOW_CONFIDENCE`. Anything else is a valid document with a matching face. The request files carry tiny JPEG-shaped images with the marker as text. |
| World-Check's answer | A last name containing `MATCH` (sanctions) or `PEP`, in capitals. Anything else is clear. |
| Faults | `PUT /_control/{idnow\|worldcheck\|corebanking}` with `mode` `healthy`, `down` (503), `error500`, `slow` (9 s, past the 8 s wait), `timeout` (never answers) or `reject` (422); `failNext: N` fails the next N calls first; `latencyMs` overrides the usual latency (2 s IDNow, 1 s World-Check, 0.2 s per core-banking call). `GET /_control/requests?provider=&keyPrefix=` is the journal of calls received, with the idempotency keys and outcomes and nothing personal. `DELETE /_control` resets everything. |

`POST /dev/token` on the simulators issues a JWT for an `officer` or `branch` staff member of a market, or a
`service`; Swagger's Authorize button takes it. Applicants don't log in: create returns an `applicationToken`
that goes in `X-Application-Token` on every later call for that application.

## What's built

```
Mobile ──► Onboarding ──► IDNow                 identity: document + face, first
                     ──► Screening ──► World-Check   sanctions and PEP; a possible match waits for an officer
                     ──► core banking            account, then card; APPROVED only after both
Branch staff (MD) ──► Onboarding                 activation after the in-person check and the wet signature
Compliance officer ──► Screening                 CLEAR or REJECT, with a reason
```

- **One call, bounded.** `POST /v1/markets/{market}/applications` with the ticket's body creates and submits
  in one go and waits up to 8 seconds, so the automated majority get `APPROVED` or `REJECTED` in the same
  response. If the answer takes longer, the response carries the current status and a `nextAction`
  (`COMPLETE_DETAILS`, `SUBMIT`, `RETAKE_PHOTO`, `WAIT`, `VISIT_BRANCH`, `NONE`); `GET` returns the rest later.
  Statuses are neutral: `DRAFT`, `IN_PROGRESS`, `ACTION_REQUIRED`, `UNDER_REVIEW`, `AWAITING_BRANCH_VISIT`,
  `APPROVED`, `REJECTED`, `WITHDRAWN`.
- **Save and resume.** A partial create is a draft on the server; `PATCH` adds the rest, `POST .../submit`
  submits. Every response lists the missing fields.
- **The review.** A possible match opens a case in Screening, due in 48 hours, that only a compliance officer
  of that market can decide. Nothing decides it automatically, overdue or not. Onboarding asks Screening for the
  outcome every minute at first, backing off to every 15 minutes.
- **Retries that never reject.** State lives in SQL; one worker per instance moves it on, claiming work with
  `UPDLOCK, READPAST` and a lease so three replicas never process the same application. Every outbound call
  has a timeout, two quick retries and a circuit breaker; a call that still fails parks the application with a
  backoff. A permanent refusal (a 4xx) stops the retries and flags the application for operations instead.
- **Idempotency.** Create needs an `Idempotency-Key`: the same key and body return the same application, a
  different body is refused with 422. Submit is safe to repeat. Screening and core banking are keyed by
  application ID; IDNow gets a key per retake.
- **Markets as config.** [`src/Atlas.Shared/atlas.json`](src/Atlas.Shared/atlas.json) holds each market's country,
  identifier rules (Annex B), confidence threshold, retake limit and activation mode. MD's branch visit is one
  value, `InBranch`. A new market is config plus its database and container; the test
  `A_new_market_needs_only_config` proves it.
- **Residency and protection.** A database and a blob container per market per service. Document images and
  the identifier are encrypted with AES-256-GCM under a key per market before they're stored; blob names are
  GUIDs. Every read, amend and erase of personal data writes an access-log row in the same save as the
  operation, attributed to an applicant, an officer, a branch member or a service; if the row can't be written,
  the operation fails. Logs carry IDs, statuses and outcomes only.
- **Erasure and retention.** A draft is deleted on request, rows and images. A submitted application is
  restricted: processing stops and the record stays until ten years after submission.
- **Everything else:** `problem+json` errors with stable codes and field-level codes from the market rules,
  request size limits, a rate limit on the one anonymous endpoint, health checks per market database and
  container, OpenAPI with Swagger UI, structured logs to Seq, CI with the same compose file, Dependabot.

## Tests

| Where | What |
|---|---|
| `src/Atlas.UnitTests` (110) | Every state transition, the field and identifier rules against the shipped `atlas.json`, the customer statuses, the backoff schedules, the cipher, the simulator scenarios, the review case. |
| `src/Atlas.IntegrationTests` (66) | The ticket's single POST; possible match → officer → approved or rejected; IDNow and World-Check outages, in-call retries, a timeout, a permanent refusal; duplicate and concurrent creates, repeated submits; save and resume; retakes and rejection; MD activation and who may do it; erasure of a draft and of a submitted application; the abandoned-upload sweep; 401, 403 across markets, every endpoint declaring its authorization, ciphertext in the blobs and the identifier column, access-log rows; validation and problem codes; an oversized body; health; OpenAPI; 50 concurrent applications, each identified once. |
| `requests/*.http` | The same scenarios by hand against the running services, including the bounded wait on both sides of its limit. |

## Left out, and known limitations

- **Production hosting.** One SQL Server and one Azurite stand in for a deployment per country. Whether the
  platform can host in each market's country is an open question; until it can, the design is residency-ready,
  not residency-compliant.
- **Credentials and keys.** `sa`, Azurite's well-known key, the JWT signing key and the encryption keys are in
  config for local runs. Production would use a managed identity per service, the bank's identity provider,
  and a key vault per market. Officers, branch staff and services get development tokens from the simulators.
- **Startup migrations** are a local convenience; production would migrate once per deployment.
- **The retention purge** (deleting restricted and expired records ten years on) is not built.
- **Erasure stops at Onboarding.** Screening keeps the screened subject and any open case under the same
  retention; the case stays in the officers' list.
- **Terms acceptance** records when, not which version.
- **Push notifications** are a seam that logs. The app finds out about a decision that took longer than one
  call when it next asks.
- **Images travel through the API** as base64, as the ticket's contract has it. Direct uploads to blob storage
  through short-lived links would be the next step for images of several MB.
- **No duplicate-applicant detection**, no check of the typed name against the document (IDNow's trimmed API
  returns no extracted data), and no check digits (the algorithms aren't confirmed).
- **A restart mid-step** waits for the claimed rows' lease to run out (two minutes) before they're picked up
  again.
- **The rate limit on create** is per client address and doesn't read forwarded headers.
- **Kestrel's own limit** answers an oversized body a little above the configured maximum without a
  `problem+json` body.

## How I used AI

Claude Code, throughout: the requirements analysis, the design, the code, the tests and these documents. The
repo carries its setup: [`CLAUDE.md`](CLAUDE.md) with the rules that come from the requirements, hooks that
format edited files, flag log calls naming personal fields, and build with warnings as errors and run the
tests before a session stops, and a read-only reviewer agent with the compliance checklist. Every decision in
`docs/reasoning.md` is one I made and can defend; the alternatives listed there are the ones I weighed.

## Note for reviewers: the supplied compose file

The compose file supplied with the task no longer starts Seq: current `datalust/seq:latest` images refuse a
first run without an admin password. This repo's [`platform/docker-compose.yml`](platform/docker-compose.yml)
fixes it by setting `SEQ_FIRSTRUN_NOAUTHENTICATION`, which matches the "credentials: none" in the supplied
README. It also adds `name: atlas`, so this stack doesn't clash with the supplied one, and Azurite (from the
allowed list) for document images. Details and the alternatives I rejected are in
[docs/reasoning.md](docs/reasoning.md#the-supplied-compose-file-no-longer-starts-seq).
