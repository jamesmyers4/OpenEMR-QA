# HANDOFF.md

Point-in-time snapshot for a fresh Claude (chat or Claude Code) session picking this project up. Read `CONTEXT.md` first for the why; `TEST-PLAN.md` is the durable coverage checklist and the definitive answer to "what's done vs. still open" — this file is the narrative complement: how to stand the environment up, and what's actually next. This file will go stale between updates — if it disagrees with `TEST-PLAN.md` or `CONTEXT.md`, trust those.

## Snapshot Summary

Updated 2026-07-25. Layer 1 (API) and Layer 2 (DB) are complete against everything originally scoped in `TEST-PLAN.md`, including the full FHIR expansion (Patient, Appointment, Encounter, AllergyIntolerance, Condition, MedicationRequest, Observation — Observation's Vitals category still only gets a bare bundle-shape check, see `CONTEXT.md`) and all cross-cutting API concerns (OAuth2, pagination, rate limiting, malformed JSON, RBAC). `FINDINGS.md` has the 14 most serious confirmed defects found along the way, ranked by severity; `API-RESPONSE-SHAPES.md` is a per-resource quick reference for create status/list envelope/not-found shape.

Layer 3 (UI) is partial. `auth.spec.ts`, `patient-registration.spec.ts`, `billing-payment.spec.ts`, and now `patient-scheduling.spec.ts` (all 3 tests, including cancel-appointment — see below) are reliably green. Several UI areas in `TEST-PLAN.md` Layer 3 remain unbuilt: reschedule/drag-drop/recurring/provider-availability, clinical encounter (SOAP note, sign/lock), billing claim generation, RBAC in the UI, patient portal, and the admin duplicate-merge tooling. The four "outside the three core layers" items (accessibility, load/perf, contract/schema validation, security/negative) and test-data-lifecycle automation are also still open.

The `AuditLogDbTests` cross-project race against `OpenEmr.Api.Tests` is resolved: `AuditLogSeedFixture` now gives `OpenEmr.Db.Tests` its own self-contained OAuth2 client + real patient-create call, so the audit-log assertion no longer depends on `OpenEmr.Api.Tests`'s timing or a non-empty database history. See `CONTEXT.md`'s Known Constraints for the full write-up.

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

## Cancel-Appointment UI Bug — Resolved

## Immediate Next Steps (roughly ordered)

1. Build the scheduled GitHub Actions workflows (daily/weekly) with failure alerting — CI currently only triggers on push/PR/manual dispatch.
2. Continue `TEST-PLAN.md`'s remaining open items: FHIR `$everything` + Bundle transaction POST; UI reschedule/drag-drop/recurring/provider-availability, clinical encounter, billing claim generation, RBAC in the UI, patient portal, admin duplicate-merge tooling (`manage_dup_patients.php`/`merge_patients.php` — no exploration done yet).
3. Accessibility, load/performance, contract/schema validation, security/negative testing, and test-data-lifecycle automation — all still open, see `TEST-PLAN.md`'s "Gaps outside the three core layers" section. The treeLine axe-core baseline (`treeLine-output/openemr-qa/reports/axe-report.md`, 607 violations across 95 pages) is a usable starting point for accessibility rather than scanning cold. Note: this session's investigation reconfirmed the test-data-lifecycle gap is real and actively causing friction, not just theoretical — a cluttered environment with many stale same-slot fixture appointments made the cancel-appointment bug materially harder to diagnose (Playwright's `.first()` on an unscoped locator kept grabbing the wrong stale appointment), and separately, orphaned rows left behind by ad hoc manual cleanup (deleting `patient_data` rows without their dependent `log`/`insurance_data` rows) transiently broke two unrelated DB tests this session until cleaned up directly.
4. Longer-term, once the above is solid: begin the deliberate "grey area" reliability-testing phase described in `CONTEXT.md` (race conditions, partial failures, timing-dependent bugs) — explicitly a later phase, not to start early.
