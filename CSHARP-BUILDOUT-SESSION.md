# CSHARP-BUILDOUT-SESSION.md — API + DB Coverage Expansion

Compiled 2026-07-20. Companion to `V2.md` (which is a point-in-time snapshot) — this file is the forward-looking work order for the next several Claude Code sessions. Read `CLAUDE.md` first, this file second; `CLAUDE.md`'s conventions govern everything below, this file only adds sequencing and resource-specific detail that isn't already written down anywhere in the repo.

State confirmed directly against `jamesmyers4/OpenEMR-QA` at commit `99b66de` (matches `V2.md` — no drift): 19/21 API+DB tests passing, 2 FHIR search tests failing with 501, one open UI bug, `tests/OpenEmr.Api.Tests` has `Patient/`, `Appointment/`, `Encounter/`, `Practitioner/` folders, `tests/OpenEmr.Db.Tests` has `Patient/` only.

## 0. Scope for this phase

**In scope:** everything under Layer 1 (API, C#/xUnit) and Layer 2 (DB, C#/xUnit) that's still `[ ]` in `TEST-PLAN.md`.

**Explicitly out of scope for this phase** — don't drift into these even if they look like quick wins:

- The open UI bug (`CalendarPage.deleteCurrentEvent()`), any new UI specs, RBAC-in-UI, patient portal UI
- Scheduled CI triggers / test-data lifecycle automation
- Accessibility, load/perf, contract/schema validation, security/negative testing (the four "outside the three core layers" items in `TEST-PLAN.md`)

These aren't being cancelled, just sequenced after this phase per the user's stated priority: build out C# breadth first, UI and infra later.

## 1. The treeLine question — resolved, not a blocker

treeLine's crawl output is a UI-only artifact (page-level metadata, no request/response capture) — this was confirmed by direct inspection in the last session, not assumed. **It has zero bearing on the API or DB layers regardless of which version of treeLine produced it.** Even a treeLine run with the new network-capture feature wouldn't change the sequencing below, because:

- The API/DB layers don't need "here are pages that exist" — they need exact endpoint shapes, which OpenEMR's own Standard API docs already give a reliable first pass at (§2 below), refined by a live curl.
- Nothing in this plan is waiting on a treeLine artifact.

If a new treeLine run happens in parallel, it's a fully independent track — treat any new output the same way `TEST-PLAN.md` already instructs for the last one: a lead to verify against the live container, never a source of truth. It doesn't need to finish before starting §3.

## 2. OpenEMR Standard API — permissions reality check

Pulled directly from OpenEMR's own `Documentation/api/STANDARD_API.md` (permissions: c=create, r=read, u=update, d=delete, s=search/list). This is the single most useful thing to read before writing the next batch of tests, because **three items in `TEST-PLAN.md`'s current wording describe operations the Standard API doesn't actually support:**

| TEST-PLAN.md line                                 | Resource's real permissions                                                | Implication                                                                            |
| ------------------------------------------------- | -------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| "Immunization: create, list-by-patient"           | `rs` — **read + search only**                                              | There is no create endpoint. The "create" half of this line can't be built as written. |
| "Procedure/Prescription: create, list-by-patient" | Procedures `rs`, Prescriptions `rs`, Drugs `rs` — **all read/search only** | Same issue — no create endpoint for either resource.                                   |
| "Message/Patient portal message: create, list"    | Patient Messages `cud` — **create, update, delete — no read, no search**   | The "list" half is the one that's impossible here, not the create half.                |

Full resource table for everything still open in Layer 1, so you don't have to re-derive it:

| Resource              | Table                                                   | Permissions | Endpoint shape                                                                                                                                                               |
| --------------------- | ------------------------------------------------------- | ----------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Facility              | `facility`                                              | `crus`      | `/apis/{site}/api/facility`, `/facility/{id}`                                                                                                                                |
| Insurance Company     | `insurance_data`                                        | `crus`      | `/apis/{site}/api/insurance_company` (top-level, not patient-nested)                                                                                                         |
| Patient Insurance     | `patient_insurance`                                     | `crus`      | `/apis/{site}/api/patient/{puuid}/insurance` (patient-nested — this is the second, separate resource `TEST-PLAN.md`'s single "Insurance" line is actually asking for two of) |
| Allergies             | `lists`                                                 | `cruds`     | `/apis/{site}/api/patient/{puuid}/allergy`                                                                                                                                   |
| Patient Immunizations | `immunizations`                                         | `rs`        | read/search only, see above                                                                                                                                                  |
| Patient Procedures    | `procedure_order`/`procedure_report`/`procedure_result` | `rs`        | read/search only, see above                                                                                                                                                  |
| Prescriptions         | `prescriptions`                                         | `rs`        | read/search only, see above                                                                                                                                                  |
| Patient Documents     | `documents`                                             | `crs`       | `/apis/{site}/api/patient/{puuid}/document` — create + read + search, no update/delete                                                                                       |
| Patient Messages      | `patient_messages`                                      | `cud`       | create/update/delete only, see above                                                                                                                                         |

**What to do with the three mismatches:** don't just quietly drop them. The right move, consistent with `CLAUDE.md`'s "update `CONTEXT.md` if a test reveals a contradiction" rule:

1. Confirm the permission table against this instance's live Swagger UI (`https://localhost:9300/swagger/`, standard section) — OpenEMR versions have moved endpoints before (see the FHIR 501 case), so confirm before assuming the public docs match this pinned image tag.
2. If confirmed, write the test that actually matches reality: e.g. `Post_Immunization_Returns_405_MethodNotAllowed` or whatever the real rejection behavior is, instead of a create-then-verify test that can't exist. That's a legitimate, valuable negative test — "confirmed this endpoint doesn't support write" is exactly the kind of finding this project has surfaced before (Practitioner-without-username, double-booking).
3. Update the `TEST-PLAN.md` line to match what's real, and log the correction in `CONTEXT.md`'s Known Constraints, same as the Appointment array-vs-object and double-booking findings.

## 3. Track A — C#/API build order

Each resource follows the established pattern exactly (see `Practitioner/PractitionerApiTests.cs` as the reference implementation): folder per resource under `tests/OpenEmr.Api.Tests/`, `[Collection("OpenEmr API")]`, constructor takes `OAuthTokenFixture`, paths built via `OpenEmrEndpoints.Rest(...)`, `JsonSerializerOptions { PropertyNamingPolicy = null }` for exact-casing payloads, unique test data via `DateTime.UtcNow.Ticks`, every assertion carries the raw response body in the FluentAssertions "because" argument, method names as `MethodUnderTest_Scenario_ExpectedResult`.

Per `TEST-PLAN.md`'s own coverage definition, each resource below should get: a happy path, a negative/edge case, and a boundary/authorization case where relevant — not just a single create-and-verify test.

1. **Facility** (`crus`, straightforward, no known gotchas) — create, get by id, update, list. Good warm-up resource before the two split-endpoint ones below.
2. **Insurance Company + Patient Insurance** — build as two separate test classes/folders (`InsuranceCompany/` and `PatientInsurance/`), not one, since they're two distinct endpoints with two distinct tables. Verify the nesting path for Patient Insurance against a live patient UUID before assuming it matches the pattern above.
3. **Allergy** (`cruds`, patient-nested, delete supported) — create, list-by-patient, delete, and a missing-required-field negative case. Good candidate for the same "confirmed by direct curl" diagnostic step used on Encounter/Practitioner if any field name is uncertain.
4. **Immunization / Procedure / Prescription** — per §2, these are read/search-only. Build fixture data via direct DB insert (same approach `DbConnectionFixture` already uses for patients) so there's something to read, then test the GET/search behavior plus the write-rejection case.
5. **Document** — `crs`, multipart upload (see OpenEMR's own example: `file`, `path`, `date` fields). Create (upload), list-by-patient, get/download a specific document. No update/delete test since the permission doesn't exist — confirm, don't assume.
6. **Message** — `cud`, no read/search. Build create, update, delete tests plus a negative test confirming there's no working list/read endpoint (turns the TEST-PLAN mismatch into an actual documented assertion instead of a silent gap).

Then, **cross-cutting concerns** (independent of any single resource, can be its own mini-track run in parallel or after):

- OAuth2: expired token rejected, invalid scope rejected, refresh token flow
- Pagination on list endpoints (pick 2-3 already-covered resources with enough seeded rows to page through)
- Malformed JSON body → 400, not 500
- RBAC: a limited-scope token can't hit an out-of-scope endpoint
- Rate limiting/throttling, if it turns out to be enabled on this instance — confirm first, don't build against an assumption

Then, **FHIR 501 investigation** (still explicitly lower priority than the REST work above): check this instance's own Swagger UI FHIR section before revisiting the OAuth-scope theory — `HANDOFF.md`'s next step, not yet done.

## 4. Track B — C#/DB build order

Each follows `PatientDbTests.cs`'s pattern: `[Collection("OpenEmr DB")]`, constructor takes `DbConnectionFixture`, prefer the insert/verify/rollback transaction pattern (`Direct_Insert_Is_Immediately_Readable_Then_Rolled_Back`) over depending on the API project having run first — the two C# projects stay independently runnable, this is non-negotiable per `CLAUDE.md`.

1. **`form_encounter`** — every row has a valid `pid` and `provider_id`. Natural pairing with the already-complete Encounter API tests; can be built without waiting on Track A.
2. **`users`** — no duplicate usernames, disabled users can't authenticate. Worth deciding here whether the existing Practitioner-username-invisibility bug (a `PractitionerService::search()` filter quirk, already documented in `CONTEXT.md`) deserves its own DB-side assertion alongside this, since it touches the same table.
3. **Soft-delete columns** (`deleted`, `date_deleted`) — verify via direct DB write, then cross-check that the API excludes soft-deleted rows from its responses. This one genuinely spans both C# projects conceptually even though the projects stay decoupled in execution.
4. **Audit/log table** — a row gets written on create/update/delete of a patient record. Flagged in `TEST-PLAN.md` as the HIPAA-relevant one worth calling out in interviews — don't skip this one for a "simpler" item.
5. **Referential integrity** — insurance ↔ patient, billing ↔ encounter. The insurance half can be seeded directly via DB insert rather than waiting on the Insurance API/fixtures from Track A — the layers are decoupled by design, so there's no real reason to sequence this after Track A item 2 even though they touch the same table.
6. **`lists`** cross-check against UI dropdowns — lowest priority in this group, most UI-adjacent, fine to leave for last.

## 5. Suggested session-to-session sequencing

Doesn't have to be rigid, but if you want a concrete order across multiple Claude Code sessions:

1. Facility (full API resource, clean warm-up)
2. Insurance Company + Patient Insurance (two classes)
3. Allergy
4. `form_encounter` DB tests (parallel track, no dependency on 1-3)
5. Immunization / Procedure / Prescription (the read-only trio — write the negative/rejection tests explicitly)
6. Document
7. Message (the no-read resource — same treatment as #5)
8. `users` + soft-delete + audit/log DB tests
9. Cross-cutting API concerns (OAuth2, pagination, malformed JSON, RBAC)
10. Referential integrity DB tests
11. FHIR 501 investigation (lowest priority of everything above)

## 6. Definition of done (unchanged from `CLAUDE.md`, restated so it's not missed)

For every new test class:

- Follows the naming and fixture conventions above
- Actually run once, not just written (`dotnet test` filtered to the class)
- Corresponding `TEST-PLAN.md` checkbox flipped, wording corrected if reality didn't match what was written (§2's three mismatches, in particular)
- Any discovered contradiction with `CONTEXT.md` gets written back into `CONTEXT.md`'s Known Constraints — same discipline that already produced the double-booking and Practitioner-username findings
