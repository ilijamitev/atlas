# Reasoning

Decisions made while building Atlas, with the alternatives I rejected. The requirements material contradicts
itself in places. This file records how I read it and what I built instead. Questions that only someone else
can answer are in [open-questions.md](open-questions.md), each with the assumption the code makes meanwhile.

- [How I read the requirements](#how-i-read-the-requirements)
- [Conflicts and how I resolved them](#conflicts-and-how-i-resolved-them)
- [Compliance confirmation](#compliance-confirmation)
- [Acceptance criteria](#acceptance-criteria)
- [Service boundaries](#service-boundaries)
- [Design notes](#design-notes)
- [Infrastructure](#infrastructure)

## How I read the requirements

There are four sources: the ticket (ATLAS-1), the #atlas-onboarding Teams thread, Group Compliance's position
GC-2026-0814, and its Annex B. Where they conflict, I ranked them:

1. **Compliance and Annex B.** GC-2026-0814 calls its points "requirements, not recommendations", and Annex B
   is part of it. Annex B's positions were also confirmed with the local regulators.
2. **Facts about the outside world.** World-Check is fast only on a clean result; a possible match goes to
   manual review, which Compliance says can take up to 48 hours. Also the provider API shapes, and the size of
   the images.
3. **Platform constraints.** One region, one SQL instance, three replicas per service (Teo). They give way to
   residency.
4. **Product requirements.** The ticket's acceptance criteria, and Nora's decisions in the thread.
5. **Suggestions in the thread.** "We'll get an exemption", "Then those ones just get rejected", "Mobile can
   hold the state locally": context, not decisions.

Where a product requirement can't be met as written, I kept its intent and changed the mechanism, as the task
invites ("If you think something in the requirements should not be built as described, do not build it as
described"). AC2 becomes a fast answer for the automated majority. AC3 becomes "people only where Compliance
requires them". AC4 becomes one codebase and one build, deployed per market.

## Conflicts and how I resolved them

### A possible sanctions or PEP match goes to a compliance officer

**What the material says:** The ticket rules out "anything involving a human reviewing anything" (AC3). GC #3
says a possible match "must be referred for manual review by a compliance officer in the relevant market. This
may take up to 48 hours. Under no circumstances may this review be automated or bypassed." In the thread, Nora
suggests rejecting those applicants, and Teo points out that auto-rejecting is what the review exists to
prevent.

**Decision:** A separate Screening service owns World-Check and the review cases. On a possible match it opens
a case, due 48 hours later, that only an officer of that market can decide: `CLEAR` or `REJECT`, with a reason.
Nothing decides a case automatically, an overdue one included. Onboarding has no code path that can clear a
match; it asks Screening for the outcome. The customer sees a neutral `UNDER_REVIEW` status that never mentions
screening.

The officer decides the outcome, not only whether the match is real. A confirmed PEP needs enhanced due
diligence, not automatic refusal, and whether confirmed PEPs may be onboarded at all is a question for
Compliance.

**Alternatives I rejected:**

- **Reject possible matches automatically** (Nora's suggestion). It is the automation GC #3 forbids, and it
  would turn away every false positive.
- **Screening inside the Onboarding service.** The officers, the match data and the rule that only an officer
  can clear a match are a separate concern. As its own service, Screening keeps match details out of
  Onboarding's database, and Onboarding can't clear a match even by mistake.
- **Rely on World-Check's own case tool.** Teo says a flagged case "goes into a manual review queue on their
  side". I can't build against a tool I don't have, and the officer's decision has to drive our state machine
  either way. If officers do work in World-Check, Screening would read their resolution instead of taking it
  through its own endpoint.

### One POST with a final answer in under three minutes

**What the material says:** "One call in, one answer out. Mobile does not want to poll and does not want a
websocket." AC2 promises approved or rejected in under three minutes. But a possible match can take 48 hours,
and MD requires a branch visit.

**Decision:** The one-call flow survives for the automated majority. A complete create, or a submit, waits up
to 8 seconds for a decision and returns it: `APPROVED` or `REJECTED` on the happy path. If the decision isn't
ready, the response carries the current status instead (`IN_PROGRESS`, `UNDER_REVIEW`,
`AWAITING_BRANCH_VISIT` or `ACTION_REQUIRED`) with a `nextAction` the app can act on, and `GET` returns the
latest status. A notifier seam, which only logs for now, marks where a push notification would go. Decision
latency is logged per market, so AC2 is measured where it can apply.

8 seconds stays under OkHttp's default 10-second read timeout, so an Android client on default settings
doesn't give up first. The wait is configurable and needs agreeing with the mobile team. A submit answers
`200` when the status is final and `202` otherwise; create always answers `201`, with the status telling the
difference. [`requests/09-bounded-wait.http`](../requests/09-bounded-wait.http) shows both sides of the limit.

**Alternatives I rejected:**

- **Hold the request until the final answer.** Fine on the happy path, impossible for a 48-hour review.
- **Return at once and make the app poll.** Simpler, but it breaks the one-call promise for the majority, who
  could have had their answer in a few seconds.
- **Websockets or server-sent events.** The mobile team ruled out websockets, and neither helps with a two-day
  review. That needs a push notification.

### Save and resume

**What the material says:** The ticket's API is one POST carrying everything, including base64 images of
several MB each (Sam). Nora calls save-and-resume "table stakes" ("Nobody's re-photographing their passport
because of a tunnel"), then suggests the app hold the state locally. Ivana: "Including the selfie and passport
scan, on the phone, unencrypted, for possibly days."

**Decision:** Drafts live on the server. `POST` creates an application with whatever the app has, `PATCH` adds
the rest, and `POST .../submit` submits it. A complete body still submits in the same call, so the ticket's
flow stays one request. The app gets a token per application to resume with. Every create carries an
`Idempotency-Key`, so a retry after a dropped connection returns the same application instead of a duplicate.
Images stay base64 in JSON, as the mobile team expects, under a request size limit.

**Alternatives I rejected:**

- **The app holds the state** (Nora's suggestion). It keeps passport and selfie images on the phone,
  unencrypted, for days, as Ivana pointed out.
- **Direct uploads to blob storage** through short-lived upload links. The right next step for images this
  size, but it changes the mobile contract further. Documented, not built.

### MD needs a branch visit

**What the material says:** Annex B allows remote identification in MD "for the application stage only". The
applicant must attend a branch, and a wet signature is required before the account may be activated. The
exemption requested in 2024 "remains outstanding. No relief should be assumed." Nora: "We'll get an exemption"
and "MD can be phase two".

**Decision:** MD is built, as configuration: `Activation: InBranch` in MD's market settings. After screening
clears, an MD application waits in `AWAITING_BRANCH_VISIT`. Branch staff record that they checked the
applicant's ID in person and collected the wet signature, and only then is the account opened and the card
ordered. Every other market activates remotely. AC1 can't hold for MD; that is listed for Product sign-off.

**Alternatives I rejected:**

- **Assume the exemption.** Annex B says not to.
- **Leave MD for phase two.** It costs one config value and one endpoint, and it proves that markets are
  configuration with a market whose flow really differs.

### One region vs data residency

**What the material says:** GC #1: personal data "must not leave the customer's country of residence". Teo:
"One region, one AKS cluster, one SQL instance", in West Europe.

**Decision:** Each market gets its own database per service and its own blob container. The market comes from
the route, and nothing crosses markets. In production, the same build is deployed in each market's country,
next to that market's database and storage account, with config choosing the market. Locally, one SQL Server
and one Azurite stand in for them. Logs carry IDs and statuses only. Whether the platform can host in each
country is an open question for Platform, because Teo's setup is a single region.

**Alternatives I rejected:**

- **One database with a market column.** Simplest, and it puts every market's personal data in one place,
  which is in at most one of the countries.
- **A schema per market in one database.** The data still sits in one place: separation in name only.

### `nationalId` is not one required string

**What the material says:** The ticket has `"nationalId": "0403991450016"`. Sam: MB's is 13 digits, MD's 10,
MC's 9 alphanumeric with a letter in position 9, ME's 10 with a different check digit, and many MF residents
have none and onboard on their passport. Annex B confirms this, and adds that identifiers "must not be used as a
primary key".

**Decision:** Records are keyed by surrogate GUIDs. The identifier is a `type` (`NATIONAL_ID` or `PASSPORT`), a
`value` and an `issuingCountry`, validated against per-market rules in config and stored encrypted. Only MF
accepts a passport in lieu. Check digits are off until the algorithms are confirmed. The JMBG-style check
digit I'd expect for MA and MB rejects the ticket's own example (it has 6 where the algorithm gives 4), so
either the example is made up or the algorithm is different. The JMBG check exists in code, switched off in
config.

**Alternatives I rejected:**

- **Validate per market, but keep one required string** (Nora's "Can we just validate per market?"). As Sam
  says, for some customers there is no national ID to put in the field.

### `country` means three different things

**What the material says:** The ticket's `"country": "MB"` is the market. The World-Check example sends
`"nationality": "MB"`. Market codes are internal, and five of the six are real ISO codes for somewhere else: MA
is Morocco, MC Monaco, MD Moldova, ME Montenegro, MF Saint Martin.

**Decision:** Three separate things. The market is in the route. `countryOfResidence` and `nationality` are ISO
3166 codes, and World-Check gets the nationality, never a market code. v1 accepts residents of the market only.
The material never says which country each market is, so config maps them to ISO's user-assigned codes XA to
XF, which can't collide with a real country. The real codes are a config change.

**Alternatives I rejected:**

- **Keep `country` and derive the nationality from it.** That is the ticket's mistake: it would screen a
  refugee in MF as an MF national, and send Moldova's code for every MD applicant.

### Access control and attribution

**What the material says:** GC #2: access restricted to authorised personnel; every access "individually
attributable to the person or system that made it" and logged; the log kept as long as the record; "Shared or
generic credentials are not acceptable".

**Decision:** Applicants get a token per application, stored only as a hash. Officers, branch staff and
services get JWTs carrying their subject, role and market, and the policies check the market against the
route. Every read, amend or erase of personal data writes an access-log row in the same save as the operation;
if the log can't be written, the operation fails. Log rows hold IDs, the actor, the action and the time, but no
personal data, and they stay as long as the record they cover.

Locally, the JWTs come from a dev token issuer in the simulator host, and SQL Server and Azurite use their shared
development credentials (`sa` and Azurite's well-known key). Those are limitations. Production would use one
managed identity per service.

### "Build in retries"

**What the material says:** "If a provider is down we obviously can't onboard anyone, so build in retries."
Teo: every service runs three replicas.

**Decision:** State lives in SQL, and a worker in each instance moves it on. Every outbound call has a timeout,
a few quick retries and a circuit breaker. If it still fails, the application keeps its status and is retried
later with backoff (`NextAttemptAt`); a technical failure never rejects anyone. A permanent error (a 4xx other
than 408 or 429) stops the retries and flags the application for operations instead. Work is claimed with
`UPDLOCK, READPAST` and a lease, so three replicas never process the same application at once, and `RowVersion`
guards every transition.

Calls to our own seams, Screening and core banking, are idempotent by application ID. The provider APIs show no
idempotency key, so a crash at the wrong moment can repeat an IDNow or World-Check call: harmless, but
billable. Onboarding sends a key anyway, and whether the providers honour one is an open question.

**Alternatives I rejected:**

- **A message broker.** See [No message broker](#no-message-broker).
- **Retry inside the customer's request.** It ties the request to a provider outage and loses the work if the
  instance dies.

### Ten-year retention vs erasure

**What the material says:** GC #4: onboarding records, "including rejected applications and all supporting
documents", are kept ten years from the date of application. GC #5: customers exercising their right to
erasure "must have their personal data deleted on request".

**Decision:** `RetentionUntil` is set at submission, ten years ahead, in both services. Erasure deletes a draft
that was never submitted at once, rows and images. A submitted application is restricted instead: processing
stops, and deletion waits for the end of retention. That follows the usual reading that an AML retention duty
overrides erasure (GDPR Art. 17(3)(b)), but it is Compliance's call. So are whether drafts count as onboarding
records, and whether "date of application" means created or submitted.

## Compliance confirmation

GC-2026-0814 asks for written confirmation that its points are reflected in the design before development
begins. This section is that confirmation.

| Point | How the design reflects it | Shown by |
|---|---|---|
| #1 Residency | A database per market per service and a blob container per market; the market comes from the route. The same build would be deployed in each country, which depends on an approved location there (open question for Platform). No personal data in logs, URLs or errors. | `HealthTests`, `SecurityTests`, the Seq search in the README |
| #2 Access and attribution | Authentication on every endpoint except create and the health checks. Market-scoped policies. An access-log row for every read, amend and erase, saved with the operation and failing closed. Individual identities, with the shared local credentials listed as a limitation. | `SecurityTests`, `ReviewFlowTests`, `BranchActivationTests` |
| #3 Manual review | Possible matches open a case in Screening that only an officer of that market can decide. No automatic decision, even when the case is overdue. | `ReviewFlowTests`, `ScreeningRecordTests` |
| #4 Retention | `RetentionUntil` = submission + 10 years, in both services. Rejected applications and every document version are kept. The purge at the end of retention is not built. | `ApplicationStateMachineTests`, `ErasureTests` |
| #5 Erasure | Drafts are deleted on request, rows and images. Submitted applications are restricted until retention ends. Open question for Compliance. | `ErasureTests` |
| #6 Annex B | Identifier rules and MD's branch activation are market config. Identifiers are never keys. | `ApplicationRulesTests`, `BranchActivationTests` |

## Acceptance criteria

| AC | Status | Shown by |
|---|---|---|
| AC1: any market, no branch visit | Met for MA, MB, MC, ME and MF. Not for MD, where Annex B requires the visit. | `HappyPathTests`, `BranchActivationTests` |
| AC2: final answer in under 3 minutes | Met for the automated majority: a few seconds, inside the 8-second wait. Not for possible matches (up to 48 hours) or MD. | `HappyPathTests`, the decision-latency log line |
| AC3: no human intervention | Met except where Compliance requires a person: possible matches (GC #3) and MD activation (Annex B). | `ReviewFlowTests` |
| AC4: one deployment, one codebase, markets as config | One codebase and one build, markets as config. Deployed per market, because of GC #1. | `MarketConfigTests`, `ValidationTests` |
| AC5: screened before an account is opened | Met: account opening comes after screening clears, in every market. | `HappyPathTests` (call order), `ApplicationStateMachineTests` |
| AC6: open account and ordered card when the call returns | Met: `APPROVED` is only set after both core-banking calls succeed. | `ApplicationStateMachineTests`, `OutageTests` |

## Service boundaries

Two services and a simulator host:

- **Onboarding** owns the customer journey: the applicant's data, the documents, the state machine, the
  worker that moves applications on, and the clients for IDNow and core banking.
- **Screening** owns the financial-crime decision: World-Check, the screened-subject snapshot, the matches,
  the review cases and the officers' decisions.
- **Simulators** stands in for IDNow, World-Check and core banking, so reviewers can drive every provider
  response over HTTP without editing code: scenario markers in the data, a fault switch per provider, a journal
  of the calls received, and the development token issuer.

`Atlas.Shared` holds what both services need the same way: market config, per-market databases, auth, the
access log, field encryption, outbound HTTP policy, logging, and the one contract they share (the screening
request and status).

The line falls between the two concerns that have different users, data and rules. Officers work only in
Screening, match data stays there, and only Screening can clear a match. Onboarding calls Screening over HTTP,
and the calls go one way only: Screening never calls back.

**Alternatives I rejected:**

- **One service.** Fewer moving parts, but the officer workflow and match data would share a database and a
  code path with the customer journey, and "only an officer can clear a match" would be a convention instead of
  a boundary.
- **A service per step** (verification, screening, accounts, cards). The IDNow and core-banking adapters are
  thin clients. Splitting them out adds network hops and distributed state with no team, scaling or data reason
  behind it.
- **In-process fakes instead of a simulator host:** an `HttpMessageHandler` per provider inside Onboarding and
  Screening, switched on in Development, with control endpoints on the services themselves. One process fewer,
  but the real clients, timeouts, retries and circuit breakers would never cross a socket, the fault switches
  would sit inside the services they exist to test, and a reviewer would have nothing to watch. As a separate
  host, the simulators are called exactly as the providers would be; only the base URLs in config differ. The
  integration tests still run all three hosts in one process, through the test server's own handler.

## Design notes

### The state machine and the worker

Every application is a row whose `State` only the methods on `Application` change, with no I/O in them. The
endpoints and the worker load the row, call one method, and save; `RowVersion` makes a stale save fail. The
states a person waits on (`Draft`, `ActionRequired`, `AwaitingBranchVisit`, the final ones) have no
`NextAttemptAt`; the states the system moves on by itself (`Verifying`, `Screening`, `OpeningAccount`,
`UnderReview`) do.

One `BackgroundService` per instance claims due rows in every market it serves (`UPDLOCK, READPAST` plus a
two-minute lease), runs one provider call per step, and saves after each step, so a crash resumes from the last
saved step. A submit wakes the local worker at once; other replicas find the work on their next one-second poll.

- **A technical failure** (timeout, network error, open circuit, 408, 429, 5xx) parks the application: same
  state, `RetryCount` up, `LastError` set, `NextAttemptAt` five seconds ahead, doubling with jitter to five
  minutes. It keeps retrying until the provider answers.
- **A permanent failure** (any other 4xx, or an answer that can't be read) stops the retries and sets
  `NeedsAttention`, with the state unchanged. Operations have to look at it; the applicant is never rejected
  for it.
- **An unexpected exception** is logged, and the lease simply runs out: the application is picked up again
  within two minutes.
- **A restart mid-step** behaves the same way: the claimed rows wait for their lease to expire. Nothing is lost
  because nothing is held in memory, and every step is idempotent on our side (Screening and core banking are
  keyed by application ID; IDNow gets the same `Idempotency-Key` per retake, whether or not it honours it).
- **A referred application** is checked with Screening after a minute, then two, four, eight, then every
  fifteen minutes. Only the status crosses over; Onboarding has no code path that turns `REFERRED` into
  anything else.
- **Decision latency** (submission to a final status) is logged per application and market, which is where
  AC2 is measured.

### Identity before screening

IDNow runs first, and World-Check only after a valid document with a matching face. A forged document or a
face mismatch therefore never reaches a sanctions list or an officer, which keeps the officers' queue for real
possible matches, at the cost of one World-Check round trip that could have run in parallel. An `INVALID`
document rejects; `INCONCLUSIVE`, a face mismatch or a confidence under the market's threshold asks for a
retake (`ACTION_REQUIRED`, `RETAKE_PHOTO`) up to the market's `MaxRetakes`, then rejects. The thread treated
the retake as a product question; the counts are config and an open question.

### Idempotent create and the application token

Every create carries a client-generated `Idempotency-Key`, stored with a SHA-256 of the request body. The
same key and body again returns the same application (`201`, same `applicationId`), finishes any upload the
first attempt didn't, and issues a new application token, which replaces the old one: only the hash of a
token is kept, so the first one can't be returned. The same key with a different body is refused with `422`.

The replay is the one way to recover the credential after a lost response, and it needs the whole original
body, which already contains everything the token protects. The alternatives were worse: keeping the token
itself (reversibly) so it can be returned again, which makes the database a credential store; or returning no
token on a replay, which leaves an app that lost the first response with an application it can never resume.

### Data model

Each market has its own database per service. Onboarding: `Applications` (the workflow: state, idempotency
key and request hash, token hash, times, `RetentionUntil`, retry and lease columns, references), `Applicants`
(the personal data, 1:1, the identifier encrypted), `Documents` (metadata only: type, version, blob name,
size, hash, `Pending` or `Stored`), `StatusHistory`, `ProviderResults` (references and outcomes, never the
data sent) and `AccessLog`. Screening: `Screenings` (the subject as screened, World-Check's case and status,
`RetentionUntil`), `ScreeningMatches`, `ReviewCases` (opened, due, decision, reason, decided by) and
`AccessLog`. Keys are GUIDs; the identifier is never a key.

### What "restricted" means

An erasure request on a submitted application restricts it (`RestrictedAt`), since the record is kept for
retention. From then on: processing stops (`NextAttemptAt` is cleared, and every transition refuses); the
applicant's `GET` still returns their own data with the status `WITHDRAWN`, and `PATCH`, submit and branch
activation answer `409 application.withdrawn`; branch staff of the market can still read it, because Annex B
needs the in-person check to be auditable; every one of those reads is access-logged like any other. Screening
keeps its own record and any open case untouched, under the same retention: an officer may still decide the
case, and Onboarding ignores the outcome. Deleting restricted and expired records at the end of retention is
not built.

## Infrastructure

### The supplied compose file no longer starts Seq

**What happened:** `docker compose up` with the compose file supplied in `/platform/` starts SQL Server, but Seq
exits immediately:

```
System.InvalidOperationException: No default admin password was supplied; set `firstRun.adminPassword` or
`SEQ_FIRSTRUN_ADMINPASSWORD`, or opt out of authentication using `firstRun.noAuthentication`/`SEQ_FIRSTRUN_NOAUTHENTICATION`.
```

Current `datalust/seq:latest` images refuse a first run unless an admin password is set, or authentication is
explicitly switched off. The supplied file only sets `ACCEPT_EULA`. Observed on 18 Oct 2026, with Docker 29.8.2
and Compose 5.6.0.

**What I changed:** in [`platform/docker-compose.yml`](../platform/docker-compose.yml):

- `SEQ_FIRSTRUN_NOAUTHENTICATION: "true"` on the Seq service. The supplied platform README lists Seq's
  credentials as "none", so this keeps that intent. Seq here is a local log sink and nothing else.
- `name: atlas` at the top. The supplied folder and this repo's folder are both called `platform`. Without a
  project name, the two stacks share container and volume names, which caused confusion while I was fixing the
  above.

Everything else is unchanged: the same two images, ports, password, healthcheck and volumes.

**Alternatives I rejected:**

- **Set `SEQ_FIRSTRUN_ADMINPASSWORD`.** It works, but it adds a credential reviewers have to know, plus a
  sign-in step before they can see any logs. That contradicts the "credentials: none" in the supplied README,
  and "if we have to guess at a manual step, we will get it wrong".
- **Pin an older Seq tag that still allows anonymous first runs.** The allowed image is `datalust/seq:latest`.
  A pinned tag is a different image from the one on the list, and it would only postpone the problem.

**If you already ran the supplied file:** its failed first run can leave a half-initialised `seq-data` volume
behind. `docker compose down -v` in the supplied `/platform/` folder removes it. This repo's stack uses its own
`atlas_*` volumes, so it's unaffected either way.

### .NET 10, not the .NET 8 you run

**What the material says:** "We run .NET 8 in production, so it is the closest match and the safest default —
but any of the three is fine and we can build and run all of them."

**Decision:** `net10.0`, the current long-term support release, supported until November 2028. .NET 8's support
ends on 10 November 2026, weeks after this was written, so a new service shouldn't start on it, and the
production estate has to move off it anyway. Nothing in the design needs 10. Moving to .NET 8 would take a
target-framework change and older packages: Swashbuckle instead of the built-in OpenAPI document, and EF Core 8,
which doesn't take a lock while migrating.

**Alternatives I rejected:**

- **.NET 8.** The closest match to production, but out of support within weeks.
- **.NET 9.** A short-term release whose support also ends in November 2026.

### Document images in blob storage (Azurite locally)

**What the material says:** the images arrive as base64 and are "going to be several MB each" (Sam). GC #4 keeps
"all supporting documents" for ten years, and GC #1 keeps them in the customer's country.

**Decision:** Images go to blob storage, one private container per market, encrypted with the market's key
before upload. The database keeps only metadata: type, version, blob name, size and hash. Locally that's
Azurite from the allowed list, blob service only. It runs with `--skipApiVersionCheck`, because Azurite rejects
storage API versions newer than it knows, which a newer `Azure.Storage.Blobs` can send. In production each
market's country gets its own storage account, reached with a managed identity instead of an account key.

A blob and a row can't be saved in one transaction, so the database stays the source of truth. The row is
written first as `Pending`, then the blob is uploaded, then the row becomes `Stored`. The worker removes
`Pending` rows older than an hour, with their blob if there is one, so no passport image is left without a
record; an upload in progress is seconds old, so the sweep can't touch it. Deletes remove the blob first,
then the row, so a retry finishes the job, and a request whose rows vanished while it was uploading (an
erasure that won the race) deletes the blobs it just wrote.

**Alternatives I rejected:**

- **Images in the market's SQL database.** One transaction and one container fewer, but several MB per image,
  kept ten years, bloats the databases and their backups, and production would move them to blob storage
  anyway.
- **Direct uploads from the app** through short-lived upload links. It keeps the images out of the API
  entirely, but it changes the mobile contract further. Documented as the next step.

### A vulnerable transitive package, pinned

The NuGet audit flagged `System.Security.Cryptography.Pkcs` 9.0.11 (CVE-2026-84008, high severity), which EF
Core brings in through `Microsoft.Data.SqlClient` 6.1.7. [`Directory.Packages.props`](../src/Directory.Packages.props)
pins the patched 10.0.13 through central transitive pinning, until SqlClient picks it up itself.

CI treats high and critical advisories as errors. A plain `dotnet build` only warns, so an advisory published
after submission can't break the README run.

### No message broker

**Decision:** The services talk over HTTP, and Onboarding's worker drives every step from the database. The only
long-running interaction between the services is the review, which can take up to 48 hours. Onboarding checks
Screening for those applications every minute at first, backing off to every 15 minutes, so an officer's
decision reaches Onboarding within 15 minutes.

**Alternatives I rejected:**

- **RabbitMQ**, which is on the allowed list. Everything people usually want from a broker, the SQL work queue
  already gives: durability, retries with backoff, safe sharing of work across replicas, and riding out a
  Screening outage. Done properly, a broker adds an outbox in each service, duplicate handling on the receiving
  side, dead-letter queues and another container, for one interaction that polling handles. Sending the
  screening request through a broker would also put personal data at rest in it: one more store to keep
  in-country, and the platform's Service Bus namespace is shared, in West Europe.
- **Callbacks from Screening to Onboarding.** Screening would then need its own retries and outbox to deliver
  them reliably. Polling keeps the dependency one-way.

If more consumers appear (push notifications, analytics), the next step is events on the platform's Service
Bus, sent through an outbox and carrying IDs only.
