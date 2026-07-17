# CONTEXT.md

Permanent reference for this project — what it is, why it's built this way, and the decisions behind it. This doesn't change week to week. For current build status and what to work on next, see `TEST-PLAN.md`.

## Purpose

Portfolio test automation suite demonstrating full-stack QA coverage — API, database, and UI — against a real, production-validated healthcare practice-management system. Structured to mirror the engineering stack and domain of a target employer (TherapyNotes: C#, ASP.NET Core, Angular/TypeScript, PostgreSQL) while testing a different real system (OpenEMR, PHP/MySQL). The point being demonstrated is the test architecture and layer-separation discipline, not a language match to the system under test — that's the accurate story to tell in an interview: "I built a C#/xUnit API+DB layer and a TS/Playwright UI layer against a production system regardless of its implementation stack," which is what SDET work actually looks like.

## System under test

OpenEMR, self-hosted via the official `openemr/openemr` Docker image against a MariaDB sidecar. REST (`/apis/{site}/api`) and FHIR R4 (`/apis/{site}/fhir`) both enabled. `DEMO_MODE=standard` seeds fixture data (patients, providers, facility) on first boot.

Domain mapping to TherapyNotes, for framing test scenarios:

| TherapyNotes concept | OpenEMR equivalent |
|---|---|
| Patient/client record | `Patient` resource / `patient_data` table |
| Appointment calendar | `Appointment` resource / `openemr_postcalendar_events` table |
| Clinical note | `Encounter` resource / `form_encounter` table |
| Provider | `Practitioner` resource / `users` table |
| Billing/claims | `Claim`, `ExplanationOfBenefit` (FHIR) / billing tables |

## Architecture — three independent layers

1. **API** (`tests/OpenEmr.Api.Tests`, C#/xUnit) — exercises both OpenEMR API surfaces directly over HTTP. Owns its own OAuth2 token acquisition per test run via `OAuthTokenFixture`.
2. **Database** (`tests/OpenEmr.Db.Tests`, C#/xUnit) — connects directly to MariaDB to verify state and referential integrity. Deliberately does not depend on the API project's execution order — DB tests either read seeded demo data or write their own fixture rows inside a rolled-back transaction.
3. **UI** (`ui/`, Playwright/TypeScript) — drives the actual OpenEMR web interface. Page-object pattern (`ui/pages/`), specs in `ui/tests/`.

The three layers are intentionally decoupled — no shared fixtures or test data contracts across the C# and TS projects. Each layer proves its own coverage independently, the way a real QA org would split ownership across API/DB and E2E specialists.

## Stack decisions

- API + DB tests: C#, xUnit, FluentAssertions, .NET 10, `dotnet test`
- API HTTP layer: `HttpClient` + OAuth2 password-grant, client dynamically registered per test run against OpenEMR's registration endpoint
- DB layer: MySqlConnector + Dapper against the MariaDB port exposed by docker-compose
- UI tests: Playwright + TypeScript, page-object pattern, HTML reporter
- CI: GitHub Actions — docker-compose stack spun up fresh per run, health-checked before tests start, torn down (`-v`, dropping volumes) after, artifacts uploaded regardless of outcome

## Conventions

- C# test naming: `MethodUnderTest_Scenario_ExpectedResult` (e.g. `Create_Patient_Missing_Required_Field_Returns_BadRequest`)
- TS test naming: plain-English `test('...')` descriptions, grouped in `test.describe` by feature
- Every C# test class using either fixture is `[Collection("OpenEmr API")]` or `[Collection("OpenEmr DB")]` — this shares one authenticated client / one DB connection across the class rather than re-authenticating or reconnecting per test
- No inline comments in code — this is a stated preference for reading/writing practice, not an oversight
- One blank line between function/method definitions, no blank lines inside a function body

## Known constraints and risks

- UI selectors in `ui/pages/` are best-effort from OpenEMR's historical form field names, not yet confirmed against a live running instance
- The Docker env-var surface (`OPENEMR_SETTING_*` names, `DEMO_MODE` behavior) has changed between OpenEMR releases historically — the pinned image version in `docker/docker-compose.yml` should be treated as a starting point, not gospel
- `DEMO_MODE=standard` reseeding only happens on a fresh volume — repeated local runs without `docker compose down -v` will accumulate test-created patients/appointments over time

## Glossary

- **pid** — OpenEMR's internal patient ID, primary key on `patient_data`
- **pc_eid** — appointment/event ID, primary key on `openemr_postcalendar_events`
- **site** — OpenEMR's multi-tenant concept; single-tenant local setups use `default`
- **flex image** — the OpenEMR Docker variant built for local/dev use with developer tooling baked in, as opposed to the production-hardened image

## Decision log

- **Why OpenEMR over a stack-matching toy project** — evaluated tutorial-scale ASP.NET Core + Angular sample apps first; they weren't robust enough to demonstrate real coverage (one candidate had no backend code committed at all). OpenEMR trades stack fidelity for genuine production-grade robustness and a real REST+FHIR API surface, which was judged the more valuable portfolio signal.
- **Why C# for API+DB but not UI** — mirrors the split on the TherapyNotes-style stack (backend in C#) while keeping UI automation in the tool actually built for it (Playwright's native language is TypeScript; its C# binding lags the JS API in tooling and community examples).
- **Why decouple the three layers instead of sharing test data/fixtures across them** — avoids brittle cross-project execution-order dependencies and mirrors how real QA orgs split ownership across API/DB and E2E specialists.
