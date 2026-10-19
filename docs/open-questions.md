# Open questions

Questions that only someone outside the team can answer, grouped by who would answer them. Each has the
assumption the code makes until it's answered. The reasoning behind the decisions is in
[reasoning.md](reasoning.md).

## Compliance

- **Does erasure (#5) override the ten-year retention (#4)?** They conflict for every submitted application.
  *Assumed:* retention wins (GDPR Art. 17(3)(b)). A submitted application is restricted on request and deleted
  when retention ends.
- **Should an erasure request reach Screening?** Screening holds the screened subject and any open case under
  the same retention. *Assumed:* its record stays untouched; a case for a withdrawn application stays in the
  officers' list, and Onboarding ignores its outcome.
- **Are drafts "onboarding records"?** If not, how long may an abandoned draft be kept? *Assumed:* drafts are
  not records. Erasure deletes them at once; abandoned drafts don't expire yet.
- **Does "date of application" mean created or submitted?** It moves the end of retention. *Assumed:*
  submitted.
- **Is the 48 hours a target or an SLA?** *Assumed:* a target. Cases carry a due time and sort by it, and an
  overdue case stays open, because GC #3 forbids deciding it automatically.
- **Is a confirmed PEP refused, or onboarded after enhanced due diligence?** *Assumed:* the officer decides
  `CLEAR` or `REJECT` with a reason; enhanced due diligence happens outside Atlas.
- **Do officers need the document images to decide a case?** *Assumed:* no. A case shows the typed details and
  World-Check's matches.
- **Can someone who doesn't live in the market apply?** *Assumed:* no; residence must be the market's country.
- **What is the minimum age?** *Assumed:* 18 in every market (config).

## Product

- **Sign-off on what changed:** AC1 doesn't hold for MD (branch visit). AC2 and AC3 don't hold for possible
  matches (an officer, up to 48 hours). AC4 becomes one build deployed per market (residency).
- **How many retakes after an inconclusive result, a face mismatch or low confidence?** *Assumed:* 2 per
  market (config), with the confidence threshold at 0.85. After that the application is rejected.
- **Which terms does the applicant accept, and do they change per market?** *Assumed:* one current version;
  the application records when they were accepted, not which version.

## Platform

- **Can Atlas run in each market's country?** GC #1 keeps personal data in the customer's country, and today's
  platform is a single West Europe region. Does Azure have a region in each market's country, and if not, what
  hosts the databases and document storage there? *Assumed:* one deployment per country is possible, each with
  its own database and storage account. Until that exists, the design is residency-ready, not compliant.
- **Which country is each market?** The material uses internal codes only. *Assumed:* ISO's user-assigned codes
  XA to XF as placeholders (MA → XA … MF → XF).
- **Is there a reverse proxy in front of the services?** The rate limit on create is per client address.
  *Assumed:* none locally; behind a proxy the forwarded address would have to be trusted first.

## Mobile

- **The API changes**, which the ticket asked to agree with the mobile team: the market in the route, the
  `Idempotency-Key` header, the application token, the identifier and country fields, and `PATCH` and submit
  for save and resume.
- **The new statuses and `nextAction` values,** and push notifications for decisions that take longer than one
  call.
- **Client timeouts.** OkHttp defaults to a 10-second read timeout. *Assumed:* the server waits at most 8 seconds
  (configurable).

## Providers

- **IDNow takes no personal details and returns no extracted data**, although the ticket says we send it "the
  images and the personal details". So nothing checks the typed name against the passport, and World-Check
  screens the name the applicant typed. *Assumed:* the trimmed API in the ticket is complete. If IDNow can return
  the document's data, Onboarding should compare it with the typed details before screening.
- **Do the providers accept idempotency keys?** *Assumed:* no. Onboarding sends one anyway; without it, a retry
  after a timeout can repeat a billable call.
- **Where do the providers process data?** GC #1 applies to them too.
- **Is `POSSIBLE_MATCH` returned at once, or via a callback after World-Check's own review?** Teo says a flagged
  case "goes into a manual review queue on their side". *Assumed:* it is returned at once, as in the ticket's
  sample, and officers decide in Atlas.
- **Which country codes does World-Check expect?** *Assumed:* ISO 3166 alpha-2.
- **Is account opening at the core bank idempotent?** *Assumed:* yes, keyed by application ID, as the simulator is.

## Annex B

- **What are the check-digit algorithms for MA, MB, ME and MD?** *Assumed:* none are checked. A JMBG-style check
  exists in code but is off, because it rejects the ticket's own MB example.
- **What is the format of MF's Personal Number?** *Assumed:* 1 to 20 letters and digits.
