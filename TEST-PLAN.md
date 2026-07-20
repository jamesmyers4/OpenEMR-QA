# Test Coverage Plan

## A note on "100% coverage" here

OpenEMR is a third-party system under test, not your own codebase, so line/branch coverage tools (coverlet, istanbul) don't apply the way they would on a project like Shenny or treeLine. "100% coverage" in this repo means 100% of the scenario matrix below — every resource, table, and workflow gets at least a happy path, a negative/edge case, and (where relevant) a boundary/authorization case. That's the metric to track, and it's also the stronger interview story: it shows you scope coverage deliberately instead of chasing a percentage a tool spits out.

Status legend: `[x]` scaffolded with a real test, `[ ]` planned, not yet written.

## Layer 1 — API (C#/xUnit)

### Legacy REST (`/apis/{site}/api`)
- [x] Patient: create, list, get by id, update, missing-required-field
- [x] Appointment: create, list-by-patient, double-booking (confirmed allowed as a soft warning, not a hard conflict)
- [ ] Appointment: update/reschedule, cancel, recurring series
- [x] Encounter: create, list-by-patient, close/sign encounter
- [x] Practitioner: list, get by id
- [x] Facility: create, get by id, update, list, missing-required-field, invalid-uuid
- [x] Insurance Company: list, create (confirmed 500 — broken in this OpenEMR version), update (same bug)
- [x] Patient Insurance: create, get by id, list, update (confirmed destructive — nulls unset fields), missing-required-field, not-found
- [ ] Allergy: create, list-by-patient, delete
- [ ] Immunization: create, list-by-patient
- [ ] Procedure/Prescription: create, list-by-patient
- [ ] Document: upload, list-by-patient, download
- [ ] Message/Patient portal message: create, list

### FHIR R4 (`/apis/{site}/fhir`)
- [x] Patient search bundle
- [x] Appointment search bundle
- [ ] Encounter, Condition, AllergyIntolerance, MedicationRequest, Observation search + read
- [ ] $everything operation on Patient (full record export — good stress test of the API layer)
- [ ] Bundle transaction POST (create multiple linked resources in one call)

### Cross-cutting API concerns
- [ ] OAuth2: expired token rejected, invalid scope rejected, refresh token flow
- [ ] Pagination on list endpoints
- [ ] Rate limiting / throttling behavior if enabled
- [ ] Malformed JSON body returns 400, not 500
- [ ] RBAC: a limited-scope token cannot hit an out-of-scope endpoint

## Layer 2 — Database (C#/xUnit)

- [x] `patient_data`: seeded demo rows present, `pid` uniqueness, required-field integrity
- [x] `openemr_postcalendar_events`: no orphaned `pc_pid` references
- [x] Transactional insert/verify/rollback pattern proven
- [ ] `form_encounter`: every encounter row has a valid `pid` and `provider_id`
- [ ] `users`: no duplicate usernames, disabled users can't authenticate (cross-check with API layer)
- [ ] `lists`: allergy/immunization code lists match what the UI dropdowns render
- [ ] Soft-delete columns (`deleted`, `date_deleted`) respected — deleted rows excluded from API responses
- [ ] Audit/log table gets a row on create/update/delete of a patient record (HIPAA-relevant behavior worth calling out in interviews)
- [ ] Referential integrity across insurance ↔ patient, billing ↔ encounter

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
- [ ] **Test data lifecycle** — a documented reset strategy so re-running the suite doesn't accumulate junk patients (right now DEMO_MODE reseeding on container recreation is the reset mechanism — worth automating a `docker compose down -v && up` step per full run)

## Suggested build order

1. Finish Layer 1 (API) resource-by-resource — it's the fastest layer to iterate on and de-risks the OAuth2/demo-data assumptions the other two layers depend on.
2. Layer 2 (DB) alongside it, since most DB tests are "verify what the API just wrote."
3. Layer 3 (UI) last, once you know appointment/patient IDs and demo credentials are stable — UI selectors are the most likely thing to need adjustment against the real running container, so validate them with `npx playwright codegen` before writing more UI specs.
