# HANDOFF.md

Point-in-time snapshot for a fresh Claude (chat or Claude Code) session picking this project up. Read `CONTEXT.md` first for the why; `TEST-PLAN.md` is the durable coverage checklist and the definitive answer to "what's done vs. still open" — this file is the narrative complement: how to stand the environment up, and what's actually next. This file will go stale between updates — if it disagrees with `TEST-PLAN.md` or `CONTEXT.md`, trust those.

## Snapshot Summary

Updated 2026-07-26. Layer 1 (API) and Layer 2 (DB) are complete against everything originally scoped in `TEST-PLAN.md`, including the full FHIR expansion (Patient, Appointment, Encounter, AllergyIntolerance, Condition, MedicationRequest, Observation — Observation's Vitals category still only gets a bare bundle-shape check, see `CONTEXT.md`) and all cross-cutting API concerns (OAuth2, pagination, rate limiting, malformed JSON, RBAC). `FINDINGS.md` has the 14 most serious confirmed defects found along the way, ranked by severity; `API-RESPONSE-SHAPES.md` is a per-resource quick reference for create status/list envelope/not-found shape.

Layer 3 (UI) is now feature-complete against everything scoped in `TEST-PLAN.md`: `auth.spec.ts`, `patient-registration.spec.ts`, `billing-payment.spec.ts`, `patient-scheduling.spec.ts` (6 tests), `clinical-encounter.spec.ts`, `billing-claim.spec.ts`, `rbac.spec.ts` (2 tests), `admin-duplicate-merge.spec.ts`, and now `patient-portal.spec.ts` are all reliably green (20 tests total; confirmed with a clean `--workers=2` run matching CI's actual concurrency), modulo the pre-existing patient-registration duplicate-check dialog flakiness under parallel load (retries absorb it — see `CONTEXT.md`). Getting the patient portal test working required real environment bootstrap, not just test code: the portal was fully disabled/misconfigured on this instance (`portal_onsite_two_enable=0`, a placeholder site address, `enforce_signin_email=1`, forced-credential-reset on) — all fixed at the DB/`docker-compose.yml` level, same category of change as the earlier FHIR/OAuth-client-enable fixes; see `CONTEXT.md`'s Known Constraints for the full write-up. A pre-existing, unrelated flake was also found and fixed along the way: `patient-scheduling.spec.ts`'s reschedule test had a hardcoded `'2026-07-25'` target date that silently became "yesterday" once real time moved past it — now computed as `new Date().toISOString().slice(0, 10)` (today) instead. (As of this 2026-07-26 update, the four "outside the three core layers" items and test-data-lifecycle automation were still open — see the 2026-07-27 update below for the current state.)

The `AuditLogDbTests` cross-project race against `OpenEmr.Api.Tests` is resolved: `AuditLogSeedFixture` now gives `OpenEmr.Db.Tests` its own self-contained OAuth2 client + real patient-create call, so the audit-log assertion no longer depends on `OpenEmr.Api.Tests`'s timing or a non-empty database history. See `CONTEXT.md`'s Known Constraints for the full write-up.

CI now runs on a schedule, not just push/PR/manual dispatch: `.github/workflows/scheduled-smoke.yml` (daily, API+DB+`auth.spec.ts` on chromium only) and `.github/workflows/scheduled-regression.yml` (weekly, the full suite across chromium+firefox) both call a shared reusable workflow (`run-suite.yml`) and auto-file/update a GitHub Issue on failure, auto-closing it on the next passing run. See `CONTEXT.md`'s Stack Decisions for the full write-up.

Updated 2026-07-27: all four items that were tracked under `TEST-PLAN.md`'s "Gaps outside the three core layers" — accessibility, load/performance, contract/schema validation, and security/negative testing — are now closed and checked off there; full detail lives in `TEST-PLAN.md` and `CONTEXT.md`, not repeated here. Briefly: `ui/tests/accessibility.spec.ts` (axe-core, gated on `critical`-impact violations) found two new real defects, `FINDINGS.md` #15 and #16; `load/appointment-booking.js` (k6) plus its `load/run-load-test.mjs` orchestrator confirmed the existing double-booking-is-a-soft-warning behavior holds under genuine concurrent load, not just sequential calls; `FhirSchemaValidator` (`tests/OpenEmr.Api.Tests/Fhir/`) validates live FHIR responses against the official, vendored `fhir.schema.json` and found a real, systemic defect, `FINDINGS.md` #17 (every Bundle's `meta.lastUpdated` is missing its timezone offset); and three security/negative checks (SQL-injection-shaped search input, XSS-shaped free-text input, session-fixation on login) all came back confirmed-safe, documented in `CONTEXT.md`'s Known Constraints. The only item remaining from that former list is test-data-lifecycle automation — see Immediate Next Steps below.

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

1. All of Layer 3's originally-scoped `TEST-PLAN.md` items are now done, including patient portal and admin duplicate-merge tooling (`manage_dup_patients.php`/`merge_patients.php`, covered by `admin-duplicate-merge.spec.ts`). See `CONTEXT.md` for what's confirmed supported vs. genuinely absent in this OpenEMR version, and for the environment-bootstrap changes (`docker-compose.yml`'s new `OPENEMR_SETTING_portal_*`/`enforce_signin_email` entries) the patient portal test required.
2. Test-data-lifecycle automation is the only item left from `TEST-PLAN.md`'s former "Gaps outside the three core layers" list — accessibility, load/performance, contract/schema validation, and security/negative testing are all done now (see the Snapshot Summary above). This gap remains real and actively causing friction — the admin duplicate-merge test surfaced a fresh instance of it: `manage_dup_patients.php`'s unconditional-on-every-load duplicate-score recompute now runs against several hundred accumulated fixture patients, and a test asserting on that page must match rows by the "Merge and Discard" option's presence, not just by a shared last name, because unrelated fixture patients can coincidentally clear the `dupscore > 12` display threshold against each other. Separately, a hardcoded date (`patient-scheduling.spec.ts`'s reschedule test) previously silently broke as real time passed it (now fixed) — worth grepping the suite for other hardcoded near-term dates before they cause the same drift, and the k6 load test added this session deliberately computes its target date dynamically to avoid repeating that mistake.
3. Longer-term, once the above is solid: begin the deliberate "grey area" reliability-testing phase described in `CONTEXT.md` (race conditions, partial failures, timing-dependent bugs) — explicitly a later phase, not to start early.
