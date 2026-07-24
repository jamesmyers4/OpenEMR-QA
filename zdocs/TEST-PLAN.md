# Test Coverage Plan

## A note on "100% coverage" here

OpenEMR is a third-party system under test, not your own codebase, so line/branch coverage tools (coverlet, istanbul) don't apply the way they would on a project like Shenny or treeLine. "100% coverage" in this repo means 100% of the scenario matrix below — every resource, table, and workflow gets at least a happy path, a negative/edge case, and (where relevant) a boundary/authorization case. That's the metric to track, and it's also the stronger interview story: it shows you scope coverage deliberately instead of chasing a percentage a tool spits out.

Status legend: `[x]` scaffolded with a real test, `[ ]` planned, not yet written.

## Layer 1 — API (C#/xUnit)

### Legacy REST (`/apis/{site}/api`)
- [x] Patient: create, list, get by id, update, missing-required-field
- [x] Appointment: create, list-by-patient, double-booking (confirmed allowed as a soft warning, not a hard conflict)
- [x] Appointment: cancel (confirmed a real hard delete via `AppointmentService::deleteAppointmentRecord()`, distinct from the UI's still-broken cancel flow); update/reschedule and recurring series are both confirmed **not supported by this REST API at all** — there is no `PUT`/`PATCH` route or controller method for this resource (confirmed 404), and `AppointmentService::insert()` only reads a fixed set of fields and silently ignores any recurrence-shaped fields in the POST body rather than creating a real series
- [x] Encounter: create, list-by-patient, close/sign encounter
- [x] Practitioner: list, get by id
- [x] Facility: create, get by id, update, list, missing-required-field, invalid-uuid
- [x] Insurance Company: list, create (confirmed 500 — broken in this OpenEMR version), update (same bug)
- [x] Patient Insurance: create, get by id, list, update (confirmed destructive — nulls unset fields), missing-required-field, not-found
- [x] Allergy: create, missing-required-field, list-by-patient (confirmed the patient-nested list/get routes are broken — always return empty; real list-by-patient coverage uses the top-level route's `puuid` filter instead), delete
- [x] Immunization: read/search-only (`rs`, no create endpoint exists — confirmed via 404 on POST), list filtered by `patient_id` works correctly, get by uuid, 404 on nonexistent uuid
- [x] Procedure: read/search-only (`rs`, confirmed 404 on POST), list/get-by-uuid work for well-formed fixtures but the list route ignores any query-string filter entirely (no real list-by-patient is possible), and both routes 500 for any row whose `encounter_id`/`provider_id` doesn't resolve to a real row (confirmed bug, `ProcedureService`)
- [x] Prescription: read/search-only (`rs`, confirmed 404 on POST), list works but also ignores any query-string filter (no real list-by-patient), and `GET /api/prescription/{uuid}` is unconditionally broken — always 500, valid uuid or not (confirmed bug, `PrescriptionService::getOne()`)
- [x] Document: upload, list-by-patient, download, missing-file, missing-path (confirmed the `path` category resolution and the missing-required-query-param cases are both real bugs — see `CONTEXT.md`), download-nonexistent, empty-category list, no update/delete route
- [x] Message: create, missing-required-field, no read/list route (confirmed 404), update (confirmed it appends rather than replaces the body, and — a real bug — never scopes by pid so a mismatched patient id in the URL still updates another patient's note), delete (confirmed soft-delete via `deleted=1`, correctly scoped by pid+id, but — a real bug — reports the same `200` success whether or not any row actually matched, including for a nonexistent message id)

### FHIR R4 (`/apis/{site}/fhir`)
- [x] Patient search bundle — genuinely passing now (see the resolved root cause below), not just the OAuth-scope workaround previously assumed
- [x] Appointment search bundle — same fix, same result
- [x] Encounter, Condition, AllergyIntolerance, MedicationRequest, Observation search + read — all 5 scopes added to `appsettings.test.json`. Encounter/AllergyIntolerance/Condition/MedicationRequest all go further than the bare Patient/Appointment bundle-shape check: each creates real fixture data (via the existing REST create endpoint for Encounter/Allergy, via a direct SQL insert for Condition/MedicationRequest since neither has one), then asserts a `patient`-filtered search actually returns that entry — not just a technically-valid bundle, which a bare unfiltered search could return even when patient-scoped filtering is silently broken. Confirmed live: the FHIR `Patient.id` is the exact same uuid as the REST `puuid` used everywhere else in this project, so no separate id-translation was needed. Observation is the one exception — its real backing table (`form_vitals`) additionally requires a `forms` table linkage row to be visible to search at all (confirmed by reading `VitalsService::search()`, which joins to `forms`), and reproducing that live via direct SQL produced an unexplained `500` not yet root-caused; `ObservationApiTests.cs` therefore only asserts the same bare-bundle-shape pattern as Patient/Appointment for now — see `CONTEXT.md` Known Constraints for the full detail and the open follow-up
- [ ] $everything operation on Patient (full record export — good stress test of the API layer)
- [ ] Bundle transaction POST (create multiple linked resources in one call)

### Cross-cutting API concerns
- [x] OAuth2: no bearer token rejected, malformed bearer token rejected, refresh token flow (confirmed requires the `offline_access` scope at registration/token-request time — omitted by default in this project's main test scope) issues a new working access token. True token-**expiry** rejection is not covered: access tokens are 1-hour JWTs signed by the server, so simulating real expiry would require either waiting out the full TTL or forging a JWT with the server's private key — neither is practical for this suite, so this is a documented, deliberate gap rather than a silent one (same rationale as the FHIR 501 deferral)
- [x] Pagination on list endpoints — confirmed `_offset`/`_limit` genuinely page through `GET /api/patient` (non-overlapping ids across pages), not just accepted-and-ignored
- [x] Rate limiting / throttling — confirmed **not enabled** on this instance (no rate/throttle-related row in the `globals` table); no test written, per the "confirm first, don't build against an assumption" guidance
- [x] Malformed JSON body returns 400, not 500 (confirmed on `POST /api/facility` with a truncated body — falls through to the normal missing-required-field validation path, not a crash)
- [x] RBAC: a token registered and granted only `user/patient.read` can call `GET /api/patient` but gets `401` on `GET /api/facility`, confirmed via `_rest_config.php`'s `scope_check()` (`user/{resource}.{permission}` string compared against the token's granted scopes, hard `401` on a miss) rather than assumed

## Layer 2 — Database (C#/xUnit)

- [x] `patient_data`: seeded demo rows present, `pid` uniqueness, required-field integrity
- [x] `openemr_postcalendar_events`: no orphaned `pc_pid` references
- [x] Transactional insert/verify/rollback pattern proven
- [x] `form_encounter`: every encounter row has a valid `pid` and `provider_id` (confirmed no DB-level FK exists on either column, same application-only-enforced pattern already seen on insurance/billing; `provider_id = 0` means unassigned and is correctly excluded from the orphan check, not treated as a dangling reference)
- [x] `users`: no duplicate populated usernames (confirmed no DB-level unique constraint on `username` — duplicates are only prevented, if at all, at the application layer), disabled users excluded by the exact `active = 1` predicate `AuthUtils.php`'s login check uses (confirmed via source)
- [x] Reference code lists for allergy/immunization — wording corrected: the real immunization vaccine reference set turned out to live in `codes`/`code_types` (the standard external-code-set mechanism, filtered to `ct_key = 'CVX'`), not in `list_options` as the original wording assumed; confirmed every stored `immunizations.cvx_code` resolves to a real CVX entry there. Allergy's reference set (`list_options` where `list_id = 'allergy_issue_list'`) is confirmed populated. This is the DB-layer version of the check — literally comparing against rendered UI dropdown HTML is a distinct, still-open UI-layer task, not something a DB test can verify
- [x] Soft-delete: only a `deleted` flag exists in this schema (`documents`, `pnotes`, `forms`, `onsite_mail`, `ar_activity`, `ccda_table_mapping`) — there is no `date_deleted` column anywhere; confirmed a soft-deleted `documents` row is excluded by the exact predicate `DocumentService::getAllAtPath()` uses (`deleted = 0`)
- [x] Audit/log table (`log`) gets a row on application-mediated patient creates — confirmed, but **only once real API-created patient history exists in the table; on a genuinely fresh, zero-history container this test races against `OpenEmr.Api.Tests` running concurrently and can fail on the very first run** (see `CONTEXT.md` Known Constraints, found via the Session 1 fresh-container check — not yet resolved, flagged for a future session). Confirmed **zero DB triggers exist anywhere in this schema** — audit logging is entirely PHP-mediated (`EventAuditLogger::newEvent()`), so a direct SQL write bypassing the app leaves no audit trail at all (HIPAA-relevant gap, not a test artifact)
- [x] Referential integrity across insurance ↔ patient, billing ↔ encounter — confirmed **neither relationship has a DB-level FK constraint** (both tables are InnoDB, which supports FKs, but none are declared); a direct insert with a nonexistent `pid`/`encounter` is silently accepted by the schema and only catchable via an application-style orphan-detection query, not a DB-enforced guarantee

## Layer 3 — UI (Playwright/TypeScript)

- [x] Auth: valid login, invalid password, empty password, logout — verified against live instance
- [x] Scheduling: create appointment shows on calendar — verified against live instance
- [x] Scheduling: double-booking same provider slot — verified as a **soft warning** (`confirm("Provider not available, use it anyway?")`), not a hard block; the checkbox label above undersold this until it was actually run. See `HANDOFF.md` for the still-flaky test and open follow-up.
- [ ] Scheduling: cancel appointment — spec exists (`patient-scheduling.spec.ts`) but currently fails; the delete action doesn't remove the event from the day view. Real bug, not yet root-caused. See `HANDOFF.md`.
- [ ] Scheduling: reschedule/drag-drop, recurring appointment series, provider-availability filtering
- [x] Patient registration: new patient intake form (happy path), required-field validation — verified against live instance
- [x] Patient registration: duplicate patient detection — verified; OpenEMR's own new-patient flow always runs a name+DOB match check and shows a review popup (even on zero matches), covered by `patient-registration.spec.ts`
- [ ] Clinical encounter: open encounter, add a SOAP note, sign/lock encounter
- [x] Billing: apply a payment — verified against live instance (`billing-payment.spec.ts`); creates a patient, opens a visit via Patient > Visits > Create Visit, then posts a cash payment via Fees > Payment and asserts the resulting receipt. Claim generation is still open, not covered by this test.
- [ ] Billing: generate a claim
- [ ] RBAC in the UI: a front-desk role can't see clinical notes, a provider role can't access admin settings
- [ ] Patient portal: separate login flow, patient can view own appointments only

## Gaps outside the three core layers (worth adding once the above is solid)

- [ ] **Accessibility** — axe-core scan on login, calendar, and patient registration pages (you already have this pattern from Shenny). `treeLine-output/openemr-qa/reports/axe-report.md` already has a full 95-page axe-core baseline (607 violations) from the treeLine crawl — worth reviewing as a starting point before writing this from scratch.
- [ ] **Load/performance** — k6 script hitting the appointment-booking endpoint under concurrent load, checking for booking conflicts you'd only see under contention (you already have this pattern from Shenny too)
- [ ] **Contract/schema validation** — validate FHIR responses against the official FHIR R4 JSON schema, not just spot-checking a few fields
- [ ] **Security/negative** — SQL-injection-shaped input into search fields, XSS-shaped input into free-text fields, session-fixation on login
- [ ] **Test data lifecycle** — a documented reset strategy so re-running the suite doesn't accumulate junk patients (right now DEMO_MODE reseeding on container recreation is the reset mechanism — worth automating a `docker compose down -v && up` step per full run). Audited directly: `Immunization`/`Procedure`/`Prescription`'s direct-SQL fixture inserts (and every API test class's patient creation, project-wide) leak permanently between runs with no cleanup, confirming this is a real, current gap and not just a hypothetical risk. The one exception is deliberate, not an oversight — `ProcedureApiTests`'s intentionally-orphaned `procedure_order` row is cleaned up in a `finally` block specifically because leaving it behind would poison every other Procedure list-based test with a `500`, a correctness requirement rather than general hygiene. `ReferentialIntegrityDbTests`' and `FormEncounterDbTests`' insert/rollback pairs are the one place in the suite that already leaves zero footprint, by construction (transactions rolled back, never committed).

## Suggested build order

1. Finish Layer 1 (API) resource-by-resource — it's the fastest layer to iterate on and de-risks the OAuth2/demo-data assumptions the other two layers depend on.
2. Layer 2 (DB) alongside it, since most DB tests are "verify what the API just wrote."
3. Layer 3 (UI) last, once you know appointment/patient IDs and demo credentials are stable — UI selectors are the most likely thing to need adjustment against the real running container, so validate them with `npx playwright codegen` before writing more UI specs.
