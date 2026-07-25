# HANDOFF.md

Point-in-time snapshot for a fresh Claude (chat or Claude Code) session picking this project up. Read `CONTEXT.md` first for the why; `TEST-PLAN.md` is the durable coverage checklist and the definitive answer to "what's done vs. still open" — this file is the narrative complement: how to stand the environment up, and what's actually next. This file will go stale between updates — if it disagrees with `TEST-PLAN.md` or `CONTEXT.md`, trust those.

## Snapshot Summary

Updated 2026-07-25. Layer 1 (API) and Layer 2 (DB) are complete against everything originally scoped in `TEST-PLAN.md`, including the full FHIR expansion (Patient, Appointment, Encounter, AllergyIntolerance, Condition, MedicationRequest, Observation — Observation's Vitals category still only gets a bare bundle-shape check, see `CONTEXT.md`) and all cross-cutting API concerns (OAuth2, pagination, rate limiting, malformed JSON, RBAC). `FINDINGS.md` has the 14 most serious confirmed defects found along the way, ranked by severity; `API-RESPONSE-SHAPES.md` is a per-resource quick reference for create status/list envelope/not-found shape.

Layer 3 (UI) is partial. `auth.spec.ts`, `patient-registration.spec.ts`, `billing-payment.spec.ts`, `patient-scheduling.spec.ts` (6 tests), `clinical-encounter.spec.ts` (SOAP note + eSign/lock), and now `billing-claim.spec.ts` (Fee Sheet → Billing Manager → CMS 1500 PDF) are all reliably green (16 tests total; confirmed with a clean `--workers=2` run matching CI's actual concurrency), modulo the pre-existing patient-registration duplicate-check dialog flakiness under parallel load (retries absorb it — and note running the whole suite with an uncapped local worker count exaggerates this well beyond what CI's `workers: 2` actually experiences, see `CONTEXT.md`). Several UI areas in `TEST-PLAN.md` Layer 3 remain unbuilt: RBAC in the UI, patient portal, and the admin duplicate-merge tooling. The four "outside the three core layers" items (accessibility, load/perf, contract/schema validation, security/negative) and test-data-lifecycle automation are also still open.

The `AuditLogDbTests` cross-project race against `OpenEmr.Api.Tests` is resolved: `AuditLogSeedFixture` now gives `OpenEmr.Db.Tests` its own self-contained OAuth2 client + real patient-create call, so the audit-log assertion no longer depends on `OpenEmr.Api.Tests`'s timing or a non-empty database history. See `CONTEXT.md`'s Known Constraints for the full write-up.

CI now runs on a schedule, not just push/PR/manual dispatch: `.github/workflows/scheduled-smoke.yml` (daily, API+DB+`auth.spec.ts` on chromium only) and `.github/workflows/scheduled-regression.yml` (weekly, the full suite across chromium+firefox) both call a shared reusable workflow (`run-suite.yml`) and auto-file/update a GitHub Issue on failure, auto-closing it on the next passing run. See `CONTEXT.md`'s Stack Decisions for the full write-up.

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

1. Continue `TEST-PLAN.md`'s remaining open items: RBAC in the UI, patient portal, admin duplicate-merge tooling (`manage_dup_patients.php`/`merge_patients.php` — no exploration done yet). FHIR `$everything`/Bundle transaction POST, UI reschedule/recurring/provider-filtering, clinical encounter (SOAP note + eSign/lock), and billing claim generation are now done — see `CONTEXT.md` for what's confirmed supported vs. genuinely absent in this OpenEMR version.
2. Accessibility, load/performance, contract/schema validation, security/negative testing, and test-data-lifecycle automation — all still open, see `TEST-PLAN.md`'s "Gaps outside the three core layers" section. The treeLine axe-core baseline (`treeLine-output/openemr-qa/reports/axe-report.md`, 607 violations across 95 pages) is a usable starting point for accessibility rather than scanning cold. Note: this session's investigation reconfirmed the test-data-lifecycle gap is real and actively causing friction, not just theoretical — a cluttered environment with many stale same-slot fixture appointments made the cancel-appointment bug materially harder to diagnose (Playwright's `.first()` on an unscoped locator kept grabbing the wrong stale appointment), and separately, orphaned rows left behind by ad hoc manual cleanup (deleting `patient_data` rows without their dependent `log`/`insurance_data` rows) transiently broke two unrelated DB tests this session until cleaned up directly.
3. Longer-term, once the above is solid: begin the deliberate "grey area" reliability-testing phase described in `CONTEXT.md` (race conditions, partial failures, timing-dependent bugs) — explicitly a later phase, not to start early.
