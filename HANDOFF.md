# HANDOFF.md

Point-in-time snapshot for a fresh Claude (chat or Claude Code) session picking this project up. Read `CONTEXT.md` first for the why; this document is the current where-things-stand. This file will go stale — treat it as accurate as of the last time it was updated, not as an ongoing source of truth. `TEST-PLAN.md` is the durable coverage checklist; a future task-specific `CLAUDE.md` will carry the actual next-action instructions for Claude Code.

## Snapshot Summary

Environment is fully working end-to-end: Docker stack (OpenEMR + MariaDB), OAuth2 authentication (including the client dynamic-registration + DB-side enable step), and the C# API + DB test layers are all confirmed functional against a live instance. Current state: **11 of 15 API/DB tests passing.** All 4 remaining failures have a known, understood cause — none are unexplained.

The UI layer (Playwright/TypeScript) is scaffolded but has not yet been run against the live instance beyond one confirmed selector fix. It has not been debugged the way the API/DB layers have.

## Environment — How To Stand It Up

```
cd docker
docker compose up -d
docker compose ps
```

Wait for both `mariadb` and `openemr` to show `healthy` — first boot after a volume reset takes several minutes (schema install + Apache startup), not seconds.

**First-login one-time steps on a fresh volume** (via browser, `https://localhost:9300`, `admin`/`pass`):

1. Product registration prompt — email optional/blank is fine, telemetry opt-in is a personal choice, no effect on tests
2. **Administration → Config → Connectors** — confirm these are checked:
   - Enable OpenEMR Standard REST API
   - Enable OpenEMR Standard FHIR REST API (enabled, but FHIR access still doesn't work — see Known Issues)
   - Site Address set to `https://localhost:9300`
   - Whether "Enable OAuth2 Password Grant" needs to be explicitly toggled on, versus the tests working regardless, has not been isolated — it was set during troubleshooting but never tested in isolation with it off. Treat as needed until proven otherwise.

Then:

```
cd ../tests
dotnet restore OpenEmr.Tests.sln
dotnet test OpenEmr.Tests.sln
```

## Current Test Status

### Passing (11)

- All `OpenEmr.Db.Tests` — fixture seeding, referential integrity, transactional insert/rollback pattern
- `Create_Patient_Returns_Created_With_New_Pid`, `Get_Patient_List_Returns_Seeded_Demo_Patients`, `Get_Patient_By_Pid_Returns_Matching_Record`, `Update_Patient_Persists_Changed_Fields`, `Create_Patient_Missing_Required_Field_Returns_BadRequest` (all Patient API tests except FHIR)
- `Create_Appointment_Returns_Created_With_New_Eid` (Appointment API)

### Failing (4) — all root-caused, none are mysteries

1. **`Fhir_Patient_Search_Returns_Valid_Bundle`** and **`Fhir_Appointment_Search_Returns_Valid_Bundle`** — both `401 Unauthorized`. REST scopes work; FHIR-specific scopes are needed but the correct scope string is unconfirmed. One attempt (`user/Patient.rs`) broke OAuth client _registration_ entirely, unrelated to the token request — reverted. This needs a clean investigation into OpenEMR's actual supported FHIR scope syntax for this version, ideally via the instance's own Swagger UI (`https://localhost:9300/swagger/`) rather than guessing at scope strings again. Low priority relative to the other three failures — REST coverage is the larger surface area.

2. **`Get_Appointments_For_Patient_Returns_Only_That_Patients_Visits`** — throws `System.InvalidOperationException: requires an element of type 'Object', but the target element has type 'Array'` when iterating the `data` array from `GET /patient/{pid}/appointment` and calling `.GetProperty("pc_pid")` on each item. The list-level `data` array itself parses fine; something about the per-item shape doesn't match the assumption baked into the original (external-docs-based) design. Needs the raw body of a real response with at least one appointment in it — that hasn't been captured yet, since the assertion only logs the raw body on top-level failures, not on this specific parsing exception. Quickest fix: temporarily print the raw string directly (e.g. via a scratch console app or an ad-hoc test) rather than guessing at field names again.

3. **`Double_Booking_Same_Slot_Same_Provider_Returns_Conflict`** — the first appointment creation succeeds as expected (`200 OK`), but the second (identical slot/provider) _also_ returns `200 OK` with a real new `id`, not the expected `409 Conflict`. This may mean OpenEMR's REST API genuinely doesn't enforce this conflict at the API layer (only in the UI's own JS validation), in which case the test's expectation is wrong and needs to change, not the implementation. Needs a UI-side check (create the same double-booking through the actual OpenEMR calendar screen) to determine whether this is a real product behavior or a payload/field issue on the test's end.

4. Two known-good-but-not-yet-fixed items were already resolved this session and shouldn't reappear: DOB field casing (fixed via explicit `JsonSerializerOptions`), and single-patient lookups using `uuid` instead of `pid` (fixed).

## UI Layer Status

Scaffolded (`ui/pages/LoginPage.ts`, `CalendarPage.ts`, specs in `ui/tests/`) but **not yet run against the live container and debugged** the way the API/DB layers have been. Known: the login submit button selector is `#login-button` (confirmed against real rendered HTML). Everything else — the calendar/scheduling page selectors especially — is unverified. This is the next layer that needs the same treatment the API layer just went through: run it, capture real failures, fix based on evidence rather than assumption.

## treeLine Integration — Status and Intent

A separate tool/repository (`treeLine`), an AI-powered Playwright-based site comprehension engine, is being extended to support authenticated crawling (currently it doesn't handle login-gated applications). Once that support exists, the plan is to point it at this project's running Docker instance and let it produce a structural inventory of OpenEMR's pages, forms, and workflows — which will then drive prioritization in `TEST-PLAN.md`, rather than continuing to guess at what's worth covering next.

Practical considerations for that integration, flagged here so they aren't rediscovered from scratch:

- The target is a **local Docker container**, not a public URL — `https://localhost:9300`, requiring the stack to be running locally when treeLine crawls it.
- The instance uses a **self-signed TLS certificate** — treeLine will need an equivalent to Playwright's `ignoreHTTPSErrors: true` (already set in `ui/playwright.config.ts`) or it will fail to connect at all.
- Authentication is a standard username/password form login (`admin`/`pass` for full access; a lower-privilege test user would be worth creating separately if treeLine's crawl is meant to also inform future RBAC test coverage).

## Immediate Next Steps (roughly ordered)

1. Resolve or intentionally reclassify the two non-FHIR API failures (appointment list shape, double-booking expectation) — both are close to done, not open-ended investigations.
2. Run the UI layer against the live instance for the first time and go through the same evidence-based debugging cycle the API layer already went through.
3. Once treeLine's authenticated-crawl support is ready, run it against this Docker instance and use its output to expand `TEST-PLAN.md` systematically.
4. Build the scheduled GitHub Actions workflows (daily/weekly) with failure alerting — CI currently only triggers on push/PR.
5. Separately investigate FHIR scope configuration — low priority, isolated from the rest of the suite.
6. Longer-term, once the above is solid: begin the deliberate "grey area" reliability-testing phase described in `CONTEXT.md` — this should not be started early.

## Collaboration Notes

- Full replacement file contents are strongly preferred over line-number-based diffs when handing back code changes — line numbers drift easily across edits and cause more confusion than they save.
- Every API-layer HTTP assertion should capture and surface the raw response body on failure (see `CONTEXT.md` conventions) — this has been the single most useful diagnostic pattern in this project so far and should be the default for any new test, not an afterthought.
- When a fix is uncertain, the established pattern in this project is to verify against the live instance (curl, or the instance's own Swagger UI at `/swagger/`) before writing code, rather than trusting external documentation that may not match this specific version's behavior. Several real bugs in this project trace back to trusting general OpenEMR documentation over the actual running instance.
