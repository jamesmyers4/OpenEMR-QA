# REVIEW-PASS-AND-FHIR-EXPANSION-SESSION.md

Compiled 2026-07-21. Companion to `CSHARP-BUILDOUT-SESSION.md`, whose full 11-item sequence is now complete. This file covers the next two things: a review/cleanup pass over everything found along the way, and expanding FHIR coverage now that it's genuinely working. Read `CLAUDE.md` first, as always — nothing here overrides its conventions.

State confirmed directly against the repo at `HEAD` (`c7a2b1a`, "FHIR 501 investigation"): 99/99 tests passing, 75 in `OpenEmr.Api.Tests`, 24 in `OpenEmr.Db.Tests` — counted directly against `[Fact]` attributes in the actual files, not just taken from the last session summary.

## Session Breakdown

This file's scope is too large for one sitting, so it's split into 4 sessions, each intended to be run as its own fresh Claude Code instance. Each entry below has a status and, once a session finishes, a short results note — read this section first if you're picking this file up cold, then jump to the referenced part for full detail.

- **Session 1 — Verify the FHIR fix survives a fresh container.** Scope: Part A5 only. Gates confidence in Session 3, since Session 3 adds 5 more FHIR tests on top of the same fix.
  **Status: done.** Confirmed: the `docker-compose.yml` env var fix genuinely survives a fresh `down -v && up`, no code change needed — both FHIR tests passed on the first run against a truly empty container. Also found and fixed one unrelated, real DB-test bug (`Direct_Insert_Billing_Row_For_Real_Encounter...` assumed ambient `form_encounter` data that doesn't exist on a fresh DB) and found-but-left-open a second one (`Application_Mediated_Patient_Inserts_Are_Represented_In_The_Audit_Log` races against `OpenEmr.Api.Tests` running concurrently — see Part A5 for full detail and the two fix options for whoever picks that up). Full suite ends at 99/99 against the fresh container. See Part A5 for the complete writeup.
- **Session 2 — Close the three Part 0 gap items.** Scope: `form_encounter` DB test, `lists` DB test, Appointment update/cancel/recurring (Part 0 below).
  **Status: done.** All three closed, all verified live against the running container, full solution ends at 110/110 (78 API + 32 DB). Highlights: `form_encounter` pid/provider_id integrity confirmed (new `FormEncounterDbTests.cs`, 5 tests); the "code lists" item turned out to be about `codes`/`code_types` (CVX) and `list_options` (`allergy_issue_list`), not the `lists` table itself — see the corrected `TEST-PLAN.md` wording and new `CodeListsDbTests.cs` (3 tests); Appointment update/reschedule and recurring series are both confirmed **not supported at all** by this REST API (no route, no controller method, recurrence fields silently ignored on create) — cancel does work, as a genuine hard delete, now covered by 3 new tests in `AppointmentApiTests.cs`. Full detail and source-level evidence in `CONTEXT.md`'s Known Constraints.
- **Session 3 — FHIR expansion (Part B).** Scope: Encounter, AllergyIntolerance, Condition, MedicationRequest, Observation search+read, in the order Part B recommends.
  **Status: not started.** Start here: read Part B in full, then `EncounterApiTests.cs`/`AllergyApiTests.cs` (both already exist with reusable patient/fixture helpers — see the pattern notes in Part B), then `appsettings.test.json`'s `Scope` string (will need the 5 new capitalized FHIR scopes added). Depends on Session 1's result (done, confirmed safe) — no further fresh-container verification needed before starting this one. Session 2 left no open threads that block this one.
- **Session 4 — Review & cleanup pass (Part A, minus A5).** Scope: A1 (response-shape reference doc), A2 (FINDINGS.md), A3 (fixture-cleanup audit), A4 (OAuth scope audit).
  **Status: not started.**

## 0. Two gaps from the original plan, owned up front — CLOSED (Session 2)

Before the review pass proper: two items from `CSHARP-BUILDOUT-SESSION.md`'s Track B, plus one from Track A, never made it into the 11-item sequencing list that was actually followed. That's a miss in the original planning doc, not something skipped mid-stream — worth closing out now rather than letting them quietly stay open forever:

- **`form_encounter` DB test** (every encounter row has a valid `pid` and `provider_id`) — **done.** `FormEncounterDbTests.cs` (5 tests: no-declared-FK check, two pure orphan-detection reads, and an insert/verify/rollback pair mirroring `ReferentialIntegrityDbTests.cs`'s existing idiom).
- **`lists` DB test** (allergy/immunization code lists match UI dropdowns) — **done, wording corrected.** The real reference data lives in `codes`/`code_types` (CVX, for immunizations) and `list_options` (`allergy_issue_list`, for allergies) — not the `lists` table itself, which is actually where patient-specific allergy/problem *records* live, a different thing entirely. `CodeListsDbTests.cs` (3 tests) confirms the CVX reference set is populated and that every real `immunizations.cvx_code` resolves to a real entry in it, plus that the allergy reference list is populated. True UI-dropdown-content comparison remains a distinct, not-yet-built UI-layer task — a DB test can't see rendered HTML.
- **Appointment: update/reschedule, cancel, recurring series** — **done, wording corrected.** Cancel is real (a hard `DELETE`, confirmed via `AppointmentService::deleteAppointmentRecord()`) and now has a test. Update/reschedule and recurring series are both confirmed **not supported by this REST API at all** — no route/controller method for the former, silently-ignored recurrence fields for the latter — and the new tests assert exactly that rather than a capability that doesn't exist. 3 new tests added to `AppointmentApiTests.cs`.

Full source-level evidence for all three lives in `CONTEXT.md`'s Known Constraints now. Full solution: 110/110 (78 `OpenEmr.Api.Tests`, 32 `OpenEmr.Db.Tests`).

## Part A — Review & improve pass

The last 11 sessions produced a genuinely large body of confirmed findings — real bugs, doc/reality mismatches, and hard-won API behavior details, all logged in `CONTEXT.md`'s Known Constraints section (now ~30 entries deep). None of that needs to be redone. What's worth doing now is organizing it, and checking a few things that were never explicitly verified along the way.

### A1. Consolidate a response-shape quick-reference

Across the 11 sessions, nearly every resource turned out to have its own answer to basic questions like "does create return 200 or 201," "does list wrap in `data` or come back as a bare array/object," "does a bad id return 400 or 404." Right now that's only discoverable by reading through all of Known Constraints. Worth adding a single reference table — either near the top of `CONTEXT.md` or as its own `API-RESPONSE-SHAPES.md` — with one row per resource and columns for: create status code, list envelope shape, single-record not-found behavior, and a one-line gotcha. This doesn't require re-verifying anything, just pulling together what's already been confirmed (Patient, Appointment, Encounter, Practitioner, Facility, Insurance Company, Patient Insurance, Allergy, Immunization, Procedure, Prescription, Document, Message all already have a confirmed answer sitting in `CONTEXT.md`).

### A2. Pull the real bugs into their own findings document

`CONTEXT.md`'s Known Constraints mixes routine "here's how this endpoint actually behaves" notes together with genuinely serious, source-confirmed defects. The following deserve to be separated out into a standalone `FINDINGS.md`, written like a real defect report (severity, repro, root cause, evidence) rather than left to compete for attention with everything else — this is strong, concrete portfolio material and it's currently buried:

- **Message `PUT` never filters by `pid`** — a token with access to one patient can silently rewrite another patient's note. Real cross-tenant authorization bug, the most serious finding in the whole set.
- **Message `PUT`/`DELETE` report `200` success on zero rows matched**, including for a nonexistent message id — silent-failure reporting, the inverse problem from the one above.
- **Document upload with a missing `path` returns `200` with a raw SQL error page as the body**, leaking server file paths — a real information-disclosure bug, not just a bad status code.
- **Document upload with a human-readable category path silently succeeds but is permanently unfindable** — the upload returns `200`/`true`, the file exists, but it can never appear in any list because the category-link row was never written.
- **`GET /api/procedure` and `GET /api/procedure/{uuid}` 500 on any row with an unresolved encounter/provider reference** — an entirely plausible real-world state that crashes the whole endpoint, not a contrived edge case.
- **`GET /api/prescription/{uuid}` is unconditionally broken** — 500s on every uuid, valid or not, due to a query referencing a nonexistent column.
- **`POST`/`PUT /api/insurance_company` always 500** — calls a method that doesn't exist anywhere in the class hierarchy.
- **`PUT /api/patient/{puuid}/insurance` is a full destructive overwrite** — any field left out of the payload gets silently nulled, even if it had a real value.
- **The patient-nested Allergy `GET` routes always return empty**, even for patients with real records — a UUID-vs-numeric-id comparison bug.
- **Zero DB triggers exist anywhere in the schema; a raw SQL write to `patient_data` produces no audit trail at all** — a real, HIPAA-relevant gap, since audit logging is entirely PHP-mediated.
- **Neither insurance↔patient nor billing↔encounter has a DB-level foreign key** — referential integrity here is a PHP-application convention only, not something the database itself enforces.
- **`billing.encounter` (`int(11)`) can't actually reference `form_encounter.encounter` (`bigint(20)`) values this project's own fixtures generate** — a latent schema mismatch.
- **FHIR was silently disabled by a one-word env var typo** (`fhir_api` vs. the real `rest_fhir_api`) since the container was first created, stacked with a second, independent case-sensitive-scope bug underneath it — worth including even though it's resolved now, since "found and fixed a masked root cause with two independent layers" is a strong story on its own.

### A3. Fixture-seeding cleanup audit

Immunization, Procedure, and Prescription seed their own fixture rows via direct `MySqlConnector` inserts from inside `OpenEmr.Api.Tests` (documented, deliberate, since none of the three has a working create endpoint). The Procedure session mentioned cleaning up its intentionally-broken fixture row in a `finally` block. Worth a quick pass confirming that same cleanup discipline is applied everywhere a direct insert happens across these three classes (and the `ReferentialIntegrityDbTests` insert/rollback pairs) — the goal is that repeated `dotnet test` runs don't quietly accumulate junk rows, which is the same concern `TEST-PLAN.md` already flags for the UI layer's test-data lifecycle.

### A4. OAuth scope string audit

`appsettings.test.json`'s `Scope` string has grown by small increments across nearly every session and is now a long single line covering both legacy REST and FHIR-style scopes. Worth a pass to confirm nothing's duplicated or unused, and consider whether it's worth a comment or a short section in `CONTEXT.md` explaining the naming split (lowercase `user/resource.read` for REST, capitalized `user/Resource.read` for FHIR) so a future session doesn't have to re-derive that distinction from scratch. Low priority, but cheap to do while everything's fresh.

### A5. Verify the FHIR fix actually survives a fresh container — CONFIRMED, resolved this session

Done via `docker compose -f docker/docker-compose.yml down -v && up -d` against a genuinely empty `dbvolume`/`sitesvolume` (Session 1). Result: **the env var fix holds on first boot, no code change needed.** Direct evidence:

- Container logs from first boot show OpenEMR's own setup script reading the env vars and applying them: `Set rest_fhir_api to 1`, `Set oauth_password_grant to 1`, `Set rest_api to 1`, `Setup Complete!` — before Apache even started.
- Confirmed independently via a direct query against the fresh DB: `SELECT gl_name, gl_value FROM globals WHERE gl_name IN ('rest_api','rest_fhir_api','oauth_password_grant')` returned `1` for all three.
- Full suite run against the fresh container: both `Fhir_Patient_Search_Returns_Valid_Bundle` and `Fhir_Appointment_Search_Returns_Valid_Bundle` passed on the very first `dotnet test` run, no manual DB patch applied. `OAuthTokenFixture` did **not** need the contingency fix (asserting `rest_fhir_api = 1` directly) — confirm there's an actual problem before adding speculative code was the right call here, since there wasn't one.
- One operational note: OpenEMR's first-boot setup (SSL cert generation + schema install) took roughly 4–6 minutes and Docker's own healthcheck (20 retries × 15s = 5 min) marked the container `unhealthy` partway through before it finished and flipped back to `healthy` on a later check. The container never restarted or failed — it just needed more time than the healthcheck retry budget allows before its first success. Worth knowing for CI: a naive "fail if unhealthy" gate could false-positive on a fresh volume; poll until `healthy` (or a timeout well beyond 5 minutes) rather than failing on the first `unhealthy` reading.

**Bonus finding, unrelated to FHIR, also only visible on a truly fresh container:** the first `dotnet test OpenEmr.Tests.sln` run came back **97/99**, not 99/99 — both failures were in `OpenEmr.Db.Tests`, not FHIR:

1. `Direct_Insert_Billing_Row_For_Real_Encounter_Passes_The_Orphan_Check_Then_Rolled_Back` failed with `Sequence contains no elements` — it assumed at least one pre-existing `form_encounter` row (`SELECT ... FROM form_encounter ORDER BY id LIMIT 1`), which doesn't exist on a container where no encounter has ever been created yet. **Fixed this session** — the test now inserts its own `form_encounter` row (referencing a real fixture-seeded patient) inside its own rolled-back transaction, matching the insert/verify/rollback idiom the rest of the file already uses, instead of assuming ambient state. Re-verified passing in isolation and as part of the full suite.
2. `Application_Mediated_Patient_Inserts_Are_Represented_In_The_Audit_Log` failed with `count > 0` false — it asserts the `log` table already has `patient-record-insert` rows, which only exist once `OpenEmr.Api.Tests`'s patient-creation tests have actually run and committed. Confirmed via direct query that after the full suite ran once, `log` had 52 such rows and the test then passed reliably on every subsequent run — this is genuinely a **race between the two test projects**, not a deterministic failure: `dotnet test OpenEmr.Tests.sln` runs `OpenEmr.Api.Tests` and `OpenEmr.Db.Tests` as separate, concurrent processes (evidenced by `OpenEmr.Db.Tests` finishing in ~200ms while `OpenEmr.Api.Tests` was still running for 11–16s), so whether this specific assertion sees the API project's writes in time is a matter of scheduling luck, not guaranteed order. **Not fixed this session, left as an open item** — the honest options are (a) accept and document this as a deliberate, narrow exception to the "two C# projects must stay independently runnable" rule (in the same spirit as `OAuthTokenFixture`'s existing documented DB dependency), since the test's entire purpose is to prove *application-mediated* writes hit the audit log and there's no way to fabricate that precondition with a raw SQL insert without defeating the point, or (b) give `OpenEmr.Db.Tests` its own minimal, independent way to trigger one real app-mediated patient create (its own OAuth client + HTTP call) so it doesn't depend on the sibling project's timing at all. This is an architectural call, not a mechanical fix — flagged here for a future session rather than decided unilaterally. On CI (which spins up a fresh stack every run, per `CONTEXT.md`), this race is live on every single run, not a one-time fluke from this session's reset — worth prioritizing.

`TEST-PLAN.md`'s audit-log line and `CONTEXT.md`'s Known Constraints have been updated to stop claiming this is unconditionally confirmed; see those files for the corrected wording.

## Part B — FHIR expansion: the remaining 5 resources

Encounter, Condition, AllergyIntolerance, MedicationRequest, Observation — search + read. Now genuinely unblocked; the reason these were never built wasn't a per-resource limitation, it was the whole FHIR surface being off.

**Follow the existing pattern exactly** — FHIR tests live as a method inside the corresponding REST resource's own test class, not a separate FHIR-specific project structure. See `Fhir_Patient_Search_Returns_Valid_Bundle` in `PatientApiTests.cs` and `Fhir_Appointment_Search_Returns_Valid_Bundle` in `AppointmentApiTests.cs`: GET via `OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, "{ResourceType}")`, assert `HttpStatusCode.OK`, assert `resourceType` equals `"Bundle"`.

Where each one belongs:

- **Encounter** → add to the existing `EncounterApiTests.cs`
- **AllergyIntolerance** → add to the existing `AllergyApiTests.cs`
- **Condition** → no existing REST test class maps to this (OpenEMR's closest REST equivalent, Medical Problem, isn't built here yet) — give it its own small class
- **MedicationRequest** → same situation; doesn't map cleanly onto `PrescriptionApiTests.cs`'s legacy REST prescriptions, so a standalone class is cleaner than overloading that file
- **Observation** → same treatment; loosely maps to Vitals, which also isn't a REST resource in this project yet

Required scope additions to `appsettings.test.json` (confirmed as valid, distinct identifiers via `ScopeRepository.php` in the earlier FHIR investigation): `user/Encounter.read`, `user/Condition.read`, `user/AllergyIntolerance.read`, `user/MedicationRequest.read`, `user/Observation.read` — capitalized, separate from the lowercase REST scopes already present.

**One thing to verify before assuming the existing bare-search pattern just works:** Patient and Appointment's existing FHIR tests do an unfiltered `GET /fhir/{Resource}` and only assert the bundle shape, not that it contains anything. These five, unlike Patient/Appointment, are inherently patient-scoped clinical resources — OpenEMR's own FHIR documentation shows them typically queried with a `patient` search parameter (e.g. `GET /fhir/Encounter?patient={id}`, `GET /fhir/Observation?patient={id}&category=vital-signs`). A bare, unfiltered search might return a technically-valid empty bundle rather than erroring, which would make a naive copy of the existing assertion pass without actually proving patient-scoped search works. Worth confirming live whether that's the case, and if so, extending the test to filter by patient and assert the bundle actually contains an entry — not just that it parses as a Bundle. Also confirm what identifier these searches expect for `patient`: OpenEMR's FHIR patient id isn't guaranteed to be the same uuid already used everywhere else in this project until checked with a real `GET /fhir/Patient` search first.

Suggested order: **Encounter → AllergyIntolerance** (both have an existing REST home and fixture data to build against) **→ Condition → MedicationRequest → Observation** (all three need a new class and a decision about what values to search on).

Two other FHIR `TEST-PLAN.md` lines remain open beyond these five — `$everything` on Patient, and Bundle transaction POST — flagging that they exist, not asking for them in this pass since you only asked about the 5 resources.

## Definition of done (unchanged, restated so it isn't missed)

Same as every prior session: naming/fixture conventions followed, each new test actually run once (not just written), `TEST-PLAN.md` checkboxes flipped with wording corrected to match reality where it doesn't, and any new contradiction with `CONTEXT.md` written back into Known Constraints — including, this time, into whichever of the two new reference documents from Part A ends up holding that material if A1/A2 get built first.
