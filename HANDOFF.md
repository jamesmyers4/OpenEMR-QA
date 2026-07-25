# HANDOFF.md

Point-in-time snapshot for a fresh Claude (chat or Claude Code) session picking this project up. Read `CONTEXT.md` first for the why; `TEST-PLAN.md` is the durable coverage checklist and the definitive answer to "what's done vs. still open" — this file is the narrative complement: how to stand the environment up, and what's actually next. This file will go stale between updates — if it disagrees with `TEST-PLAN.md` or `CONTEXT.md`, trust those.

## Snapshot Summary

Updated 2026-07-25. Layer 1 (API) and Layer 2 (DB) are complete against everything originally scoped in `TEST-PLAN.md`, including the full FHIR expansion (Patient, Appointment, Encounter, AllergyIntolerance, Condition, MedicationRequest, Observation — Observation's Vitals category still only gets a bare bundle-shape check, see `CONTEXT.md`) and all cross-cutting API concerns (OAuth2, pagination, rate limiting, malformed JSON, RBAC). `FINDINGS.md` has the 13 most serious confirmed defects found along the way, ranked by severity; `API-RESPONSE-SHAPES.md` is a per-resource quick reference for create status/list envelope/not-found shape.

Layer 3 (UI) is partial. `auth.spec.ts`, `patient-registration.spec.ts`, and `billing-payment.spec.ts` are reliably green. `patient-scheduling.spec.ts` is 2-of-3 green — the cancel-appointment case is a real, root-caused-but-unfixed bug (see below). Several UI areas in `TEST-PLAN.md` Layer 3 remain unbuilt: reschedule/drag-drop/recurring/provider-availability, clinical encounter (SOAP note, sign/lock), billing claim generation, RBAC in the UI, patient portal, and the admin duplicate-merge tooling. The four "outside the three core layers" items (accessibility, load/perf, contract/schema validation, security/negative) and test-data-lifecycle automation are also still open.

One open architectural question, not yet decided: `AuditLogDbTests.Application_Mediated_Patient_Inserts_Are_Represented_In_The_Audit_Log` races against `OpenEmr.Api.Tests` (the two C# projects run concurrently under `dotnet test OpenEmr.Tests.sln`) and can fail on the very first run against a zero-history database. See `CONTEXT.md`'s Known Constraints for the two candidate fixes.

## Environment — How To Stand It Up

```
cd docker
docker compose up -d
docker compose ps
```

Wait for both `mariadb` and `openemr` to show `healthy` — first boot after a volume reset takes several minutes (schema install + Apache startup), not seconds, and the `openemr` container can report `unhealthy` for 2-6 minutes before flipping to `healthy`. This is normal (confirmed via container logs — it's still running its own setup script, not crashing); poll past the first `unhealthy` reading rather than treating it as a failure.

**First-login one-time steps on a fresh volume** (via browser, `https://localhost:9300`, `admin`/`pass`):

1. Product registration prompt — email optional/blank is fine, telemetry opt-in is a personal choice, no effect on tests.
2. **Administration → Config → Connectors** — confirm these are checked:
   - Enable OpenEMR Standard REST API
   - Enable OpenEMR Standard FHIR REST API
   - Site Address set to `https://localhost:9300`
   - Whether "Enable OAuth2 Password Grant" needs to be explicitly toggled on, versus the tests working regardless, has never been isolated — it was set during early troubleshooting but never tested in isolation with it off. Treat as needed until proven otherwise.

Then:

```
cd ../tests
dotnet restore OpenEmr.Tests.sln
dotnet test OpenEmr.Tests.sln
```

For the UI layer:

```
cd ../ui
npm install
npx playwright install
npx playwright test
```

## Known, Real, Unresolved UI Bug — Next UI Session Should Start Here

**Canceling an appointment does not remove it from the day view.** `CalendarPage.deleteCurrentEvent()` clicks `#form_delete` inside the event frame, the native `confirm(...)` dialog is accepted, but the row persists in `openemr_postcalendar_events` afterward — confirmed via direct DB check, not a timing issue. Next step: read `deleteEvent()`/`SubmitForm()` in the container's `interface/main/calendar/add_edit_event.php` (`docker exec docker-openemr-1 grep -n "function deleteEvent" -A 40 /var/www/localhost/htdocs/openemr/interface/main/calendar/add_edit_event.php`, `MSYS_NO_PATHCONV=1` prefix needed in Git Bash to stop path-mangling) — likely needs the same `top.restoreSession()` call the save flow required, or the delete AJAX call needs a different completion signal.

## Immediate Next Steps (roughly ordered)

1. Root-cause the cancel-appointment UI bug above.
2. Decide and implement a fix for the `AuditLogDbTests` cross-project race (see Snapshot Summary).
3. Build the scheduled GitHub Actions workflows (daily/weekly) with failure alerting — CI currently only triggers on push/PR/manual dispatch.
4. Continue `TEST-PLAN.md`'s remaining open items: FHIR `$everything` + Bundle transaction POST; UI reschedule/drag-drop/recurring/provider-availability, clinical encounter, billing claim generation, RBAC in the UI, patient portal, admin duplicate-merge tooling (`manage_dup_patients.php`/`merge_patients.php` — no exploration done yet).
5. Accessibility, load/performance, contract/schema validation, security/negative testing, and test-data-lifecycle automation — all still open, see `TEST-PLAN.md`'s "Gaps outside the three core layers" section. The treeLine axe-core baseline (`treeLine-output/openemr-qa/reports/axe-report.md`, 607 violations across 95 pages) is a usable starting point for accessibility rather than scanning cold.
6. Longer-term, once the above is solid: begin the deliberate "grey area" reliability-testing phase described in `CONTEXT.md` (race conditions, partial failures, timing-dependent bugs) — explicitly a later phase, not to start early.
