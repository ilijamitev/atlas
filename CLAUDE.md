# Atlas onboarding: working notes for Claude Code

Backend for Atlas, digital customer onboarding for a banking group in six markets (MA–MF). .NET 10, two
services plus a simulator host, on SQL Server, Azurite and Seq from `platform/docker-compose.yml`.

## Map

Everything the solution owns is in `src/`: `Atlas.slnx`, the props files, `.editorconfig` and the six projects.

- `src/Atlas.Onboarding`: the customer journey. Applications API, the state machine, the worker, document
  storage (Azurite), and the IDNow, Screening and core-banking clients.
- `src/Atlas.Screening`: World-Check screening and the compliance officers' review cases.
- `src/Atlas.Simulators`: stand-ins for IDNow, World-Check and core banking, with fault control, a request
  journal and a dev token issuer.
- `src/Atlas.Shared`: market config (`atlas.json`), per-market databases, auth, the access log, field
  encryption, outbound HTTP policy, logging, the screening contract.
- `src/Atlas.UnitTests` (no infrastructure) and `src/Atlas.IntegrationTests` (all three apps in memory,
  against the compose stack).
- `docs/reasoning.md` (decisions and rejected alternatives), `docs/open-questions.md`, `requests/*.http`
  (the scenarios by hand).

## Commands

- Infrastructure: `docker compose -f platform/docker-compose.yml up -d --wait`
- Build as CI does: `dotnet build src/Atlas.slnx -p:TreatWarningsAsErrors=true`
- Unit tests: `dotnet test src/Atlas.UnitTests`
- Integration tests (need the compose stack): `dotnet test src/Atlas.IntegrationTests`
- Everything: `scripts/run.ps1` or `bash scripts/run.sh`
- New migration: `dotnet tool restore`, then
  `dotnet ef migrations add <Name> --project src/Atlas.Onboarding --output-dir Persistence/Migrations`

## Rules that come from the requirements

- Personal data stays in its market's own database and blob container (GC-2026-0814 #1).
- Every read, amend or erase of personal data writes an access-log row, saved together with the operation. If
  the log can't be written, the operation fails (#2).
- Only a compliance officer of that market decides a possible sanctions or PEP match (#3).
- Records are kept 10 years from submission (#4); erasure deletes drafts and restricts submitted applications
  (#5).
- Market differences live in `src/Atlas.Shared/atlas.json`, not in code (AC4, Annex B).
- A technical failure never changes an application's status: park it and retry.

## Never

- realistic personal data, anywhere: samples, tests, docs
- personal data in logs, exception messages or URLs
- national IDs as keys
- a code path that decides a possible match without an officer
- Docker images outside the allowed list in the task README
- copying the requirements material (`../atlas`) into the repo
- `git commit` or `git push` unless the user asks

## How we work

- Claude builds; the user reviews the diff and commits.
- Update `docs/reasoning.md` in the same change as the code it explains, in the format the file already uses:
  what the material says, the decision, the alternatives rejected. Questions for someone outside the team go
  to `docs/open-questions.md` with the assumption the code makes meanwhile.
- When a step looks finished, suggest `/step-done`.
- `src/Atlas.slnx` lists the projects at its root, with no solution folders for them (`dotnet sln add
  --in-root`). Solution Items holds only the solution-wide build files next to it: `.editorconfig`,
  `Directory.Build.props`, `Directory.Packages.props`.
- The fewest moving parts that still resolve every conflict. Feature folders, minimal APIs, file-scoped
  namespaces, nullable on. Comments explain why, never what.
