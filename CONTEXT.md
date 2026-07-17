# CONTEXT.md

Permanent reference for this project — what it is, why it exists, and the reasoning behind its major decisions. This document should stay accurate but doesn't need to change week to week. For current build status and active work, see `HANDOFF.md`. For the coverage checklist, see `TEST-PLAN.md`.

## Purpose & Vision

This is a full-stack test automation portfolio project demonstrating professional-grade QA/SDET engineering across three independently-owned layers — API, database, and UI — against a real, production-validated healthcare practice-management system (OpenEMR), chosen as a stand-in for the class of application TherapyNotes and similar companies run in production (scheduling, patient/clinical records, billing, provider workflows).

The goal is not a one-time green checkmark. The intent is a living test suite that:

1. **Runs on a schedule, not just on demand.** GitHub Actions workflows on a recurring cadence (daily smoke run, weekly full regression), with failure notifications, so the suite behaves like a real team's CI rather than a portfolio artifact that only gets exercised when someone happens to look at it.
2. **Grows its coverage systematically, not opportunistically.** Rather than hand-picking test cases, the plan is to use `treeLine` (a separate AI-powered Playwright-based site comprehension tool, currently being extended to support authenticated crawling) to generate a structural inventory of the running application — every page, form, and workflow — and use that inventory to drive what `TEST-PLAN.md` covers next, instead of guessing at what might be worth testing.
3. **Expands toward "grey area" reliability testing as a deliberate later phase.** Initial priority is straightforward, well-understood coverage: happy paths, obvious negative cases, and basic boundary conditions across all three layers. Once that base is solid, the plan is to deliberately harden toward the kind of edge cases that don't show up in a spec but cause real incidents — race conditions, partial failures, unusual concurrent access, timing-dependent bugs — in the spirit of aerospace/safety-critical software engineering culture (the NASA lessons-learned tradition of treating "it worked in testing" as the beginning of scrutiny, not the end of it). This is explicitly a later phase, not part of the initial build-out.

The architectural choice to write API and DB tests in C#/xUnit against a UI layer in TypeScript/Playwright is deliberate: it demonstrates that solid SDET practice doesn't require the test code to share a language with the system under test. OpenEMR itself is PHP; the tests treat it as a black box being verified from the outside, the way a QA team actually operates in most real organizations.

## System Under Test & Domain Mapping

OpenEMR, self-hosted via the official `openemr/openemr` Docker image against a MariaDB sidecar. REST (`/apis/{site}/api`) and FHIR R4 (`/apis/{site}/fhir`) API surfaces both exist; REST is fully working as of the latest test run, FHIR access is not yet resolved (see `HANDOFF.md`).

Domain mapping to TherapyNotes-style practice-management software, for framing test scenarios:

| TherapyNotes concept  | OpenEMR equivalent                                           |
| --------------------- | ------------------------------------------------------------ |
| Patient/client record | `Patient` resource / `patient_data` table                    |
| Appointment calendar  | `Appointment` resource / `openemr_postcalendar_events` table |
| Clinical note         | `Encounter` resource / `form_encounter` table                |
| Provider              | `Practitioner` resource / `users` table                      |
| Billing/claims        | `Claim`, `ExplanationOfBenefit` (FHIR) / billing tables      |

## Architecture — Three Test Layers Plus One Supporting Tool

1. **API** (`tests/OpenEmr.Api.Tests`, C#/xUnit) — exercises OpenEMR's REST and FHIR surfaces directly over HTTP. Owns its own OAuth2 token acquisition per test run via `OAuthTokenFixture`, including a one-time DB-side client-enablement step (see Decision Log).
2. **Database** (`tests/OpenEmr.Db.Tests`, C#/xUnit) — connects directly to MariaDB to verify state, referential integrity, and schema-level assumptions. Seeds its own known fixture patients rather than depending on OpenEMR demo data (which the production image doesn't provide) or on the API test project's execution order.
3. **UI** (`ui/`, Playwright/TypeScript) — drives the actual OpenEMR web interface. Page-object pattern. As of the last handoff, scaffolded but not yet validated against the live running instance beyond a single confirmed selector fix.
4. **treeLine** (separate repository/tool, integration in progress) — not a test layer itself, but the planned mechanism for discovering what the other three layers should cover. Currently being extended to support authenticated crawling (login with username/password) so it can be pointed at this project's Dockerized OpenEMR instance and produce a structural map of the application to drive `TEST-PLAN.md` prioritization.

The three test layers are intentionally decoupled — no shared fixtures or test-data contracts across the C# and TS projects, and the two C# projects don't depend on each other's execution order either. Each layer proves its own coverage independently, mirroring how real QA organizations split ownership across API/DB and E2E specialists.

## Stack Decisions

- API + DB tests: C#, xUnit, FluentAssertions, .NET 10, `dotnet test`
- API HTTP layer: `HttpClient` + OAuth2 password-grant, client dynamically registered per test run against OpenEMR's registration endpoint, with an explicit DB-side enable step (see Decision Log — this is not optional)
- DB layer: MySqlConnector + Dapper against the MariaDB port exposed by docker-compose
- UI tests: Playwright + TypeScript, page-object pattern, HTML reporter
- CI: GitHub Actions — currently runs on push/PR; scheduled (daily/weekly) triggers with alerting are planned but not yet built (see `HANDOFF.md`)

## Conventions

- C# test naming: `MethodUnderTest_Scenario_ExpectedResult` (e.g. `Create_Patient_Missing_Required_Field_Returns_BadRequest`)
- TS test naming: plain-English `test('...')` descriptions, grouped in `test.describe` by feature
- Every C# test class using either fixture is `[Collection("OpenEmr API")]` or `[Collection("OpenEmr DB")]` — shares one authenticated client / one DB connection across the class
- No inline comments in code — deliberate choice for reading/writing practice, not an oversight
- One blank line between function/method definitions, no blank lines inside a function body
- Every HTTP assertion in the API layer captures the raw response body and passes it into the FluentAssertions `.Because()` argument (`"response body was: {0}", raw`) — this was adopted after repeated cases where a bare status-code mismatch gave no way to diagnose the real cause without a separate manual `curl` reproduction. New API tests should follow this pattern by default.
- JSON payloads sent from the API layer explicitly pass a `JsonSerializerOptions` with `PropertyNamingPolicy = null` (see Decision Log — property casing was silently altered by .NET defaults in a way that caused real, hard-to-diagnose failures)

## Known Constraints and Risks

- **OAuth2 dynamically-registered clients default to `is_enabled = 0`** in the `oauth_clients` table and require manual approval by design (there's a real regulatory basis for this — non-patient apps require approval per relevant jurisdictional rules). `OAuthTokenFixture` handles this automatically via a direct DB update immediately after registration. This is a deliberate, narrow, documented exception to the API-layer's general independence from the DB layer — it is environment bootstrap, not a test assertion.
- **`patient_data.pid` is not a MySQL auto-increment column.** It's application-assigned (`MAX(pid) + 1`), separate from the table's actual auto-increment key (`id`). Any direct SQL insert must compute and supply `pid` explicitly.
- **Single-patient REST endpoints (`GET`/`PUT /patient/{id}`) key on the patient's UUID, not the numeric `pid`.** The `pid` works for list/create/nested-resource endpoints, but not for direct single-record lookup or update. This cost significant debugging time before being confirmed via the API's own error messages.
- **`DEMO_MODE` does not work on the production image tag** (`openemr/openemr:7.0.3`) — it's documented as a flex-series-only setting. The DB layer seeds its own known fixture data instead, which is more deterministic anyway.
- **FHIR API access is unresolved.** REST scopes (`user/patient.read`, etc.) work; FHIR endpoints return 401 even with `api:fhir` in the requested scope. An attempt to add explicit FHIR-style scopes (`user/Patient.rs`) to the OAuth client registration request broke registration entirely (`invalid_client_metadata`), so that scope string is wrong in some way not yet diagnosed. Currently reverted to the known-working REST-only scope list; FHIR access is a deferred, separately-scoped problem.
- **Appointment creation's actual response shape differs from the initial (external-docs-based) assumption.** Real behavior: `200 OK` with body `{"id": N}`, not `201 Created` with a `data.pc_eid` envelope. Fixed in code; documented here so future additions to this resource don't repeat the wrong assumption.
- **The appointment list endpoint's per-item JSON shape is not yet confirmed** — a `GetProperty` call on individual list items throws an object/array type mismatch, meaning the actual per-item structure differs from what was assumed. Needs a raw-body inspection to resolve; deferred rather than guessed at further (see `HANDOFF.md`).
- **UI selectors in `ui/pages/` are mostly unverified against the live instance** — only the login page's submit-button selector has been confirmed correct so far.

## Glossary

- **pid** — OpenEMR's internal patient ID, primary key on `patient_data`, application-assigned rather than auto-increment
- **uuid** — the identifier OpenEMR's single-patient REST endpoints actually key on (distinct from `pid`)
- **pc_eid** — appointment/event ID, primary key on `openemr_postcalendar_events`
- **site** — OpenEMR's multi-tenant concept; single-tenant local setups use `default`
- **flex image** — the OpenEMR Docker variant built for local/dev use with developer tooling baked in, as opposed to the production-hardened image this project uses
- **treeLine** — the separate AI-powered Playwright-based site comprehension tool being extended with authenticated-crawl support, intended to generate the structural map that drives future `TEST-PLAN.md` prioritization

## Decision Log

- **Why OpenEMR over a stack-matching toy project** — evaluated tutorial-scale ASP.NET Core + Angular sample apps first; they weren't robust enough to demonstrate real coverage (one candidate had no backend code committed at all). OpenEMR trades stack fidelity for genuine production-grade robustness and a real REST+FHIR API surface.
- **Why C# for API+DB but not UI** — mirrors a TherapyNotes-style C# backend stack while keeping UI automation in the tool actually built for it.
- **Why decouple the three layers instead of sharing test data/fixtures across them** — avoids brittle cross-project execution-order dependencies and mirrors how real QA orgs split ownership across API/DB and E2E specialists.
- **Why the API layer has a narrow, direct DB dependency in `OAuthTokenFixture`** — OpenEMR's manual-approval requirement for OAuth clients has no API-level bypass; a direct DB update is the only way to make a fresh environment usable without a manual UI click on every container boot, which would break CI entirely.
- **Why every API assertion captures the raw response body** — the single biggest time cost in this project's build-out was blind guessing against bare status-code mismatches. Every real root cause found (OAuth client-secret omission, DOB casing, UUID-vs-pid, appointment response shape) was only confirmed once the actual response body was visible. This is now a standing convention, not a one-off fix.
- **Why JSON payloads explicitly set `PropertyNamingPolicy = null`** — a payload containing `DOB` (the only all-uppercase field among otherwise-lowercase field names) was silently transformed by .NET's default JSON serialization in a way that caused a specific, hard-to-diagnose validation failure. Explicit casing control removes the ambiguity for any future field, not just this one.
- **Why the FHIR scope investigation was abandoned mid-stream rather than pushed further** — the first attempted fix broke a different, previously-working part of the system (client registration itself). Rather than keep guessing at OAuth scope strings, the decision was to revert to the known-good state and treat FHIR access as a separately-scoped follow-up, prioritizing a stable, mostly-green baseline over chasing one lower-priority feature.
