# Where to look

**Start with these four files.**

1. [`src/Atlas.Onboarding/Applications/Application.cs`](src/Atlas.Onboarding/Applications/Application.cs): the
   state machine. Every transition is a method with no I/O; the endpoints and the worker only load, call one
   method and save.
2. [`src/Atlas.Onboarding/Workflow/ApplicationSteps.cs`](src/Atlas.Onboarding/Workflow/ApplicationSteps.cs):
   what happens between the states. One provider call per step, saved after each, parked on a technical
   failure, flagged on a permanent one.
3. [`src/Atlas.Screening/Reviews/ReviewEndpoints.cs`](src/Atlas.Screening/Reviews/ReviewEndpoints.cs): the
   compliance officer's side of a possible match, the one thing the ticket said would not exist.
4. [`src/Atlas.Shared/atlas.json`](src/Atlas.Shared/atlas.json): the six markets as configuration, including
   MD's branch activation and the identifier rules from Annex B.

**The decision to ask me about:** keeping the ticket's single `POST` while Compliance forces a review that
can take 48 hours. A create or submit waits up to 8 seconds and returns the final answer for the automated
majority; everyone else gets a neutral status and a `nextAction`, and an officer decides the rest in a separate
service. The reasoning and the rejected alternatives are in
[`docs/reasoning.md`](docs/reasoning.md#one-post-with-a-final-answer-in-under-three-minutes).

**Not production-ready, by design:** `Atlas.Simulators` (fake providers, a fake token issuer), the shared
development credentials in `atlas.json` and `appsettings.json`, encryption keys in config, startup migrations,
and one SQL Server and one Azurite standing in for a deployment per country. The README's "Left out" section
has the full list.
