# FINDINGS.md

Standalone write-ups for the most serious, source-confirmed defects found while building this test suite — pulled out of `CONTEXT.md`'s Known Constraints, where they were competing for attention with routine "here's how this endpoint actually behaves" notes. Every finding here was confirmed against the live instance (a real `curl`/API call, a direct DB query, or both) and root-caused by reading the actual OpenEMR PHP source, not guessed at from documentation. Ordered by severity, most serious first. Each finding names the automated test that demonstrates it, where one exists.

## 1. Message `PUT` never filters by patient — cross-tenant write (COMPLETED)

**Severity:** Critical
**Status:** Open
**Component:** `MessageService::update()` (`src/Services/MessageService.php`), reached via `PUT /apis/{site}/api/patient/{pid}/message/{mid}`

**Summary:** A caller with a valid token can edit any message by `mid` through *any* patient's URL — the `{pid}` in the route is decorative, not an authorization boundary. A user or integration scoped to one patient can silently rewrite another patient's clinical note.

**Repro:**
1. Create two patients, A and B.
2. Create a message under patient A (`POST /api/patient/{A}/message`), capturing the returned `mid`.
3. Call `PUT /api/patient/{B}/message/{mid}` with a new body, using patient B's pid in the URL even though the message belongs to A.
4. Response is `200 OK`. Querying `pnotes` directly shows the message body genuinely changed.

**Root cause:** `MessageService::update()` runs `UPDATE pnotes SET ... WHERE id=?`, binding only `$mid` — the `$pid` argument is accepted by the method signature but never referenced in the SQL. Confirmed by reading the method body directly.

**Impact:** A real cross-tenant data-integrity/authorization bug. In a multi-provider or multi-tenant deployment, this means clinical note content is not isolated by patient the way the API's own URL shape implies it is.

**Automated coverage:** `Put_Message_With_Mismatched_Patient_Id_Still_Updates_Record` in `MessageApiTests.cs` — asserts this exact behavior directly rather than treating it as a should-be-blocked case, since the point is to document the real, current behavior.

---

## 2. Document upload with a missing `path` leaks server file paths (COMPLETED)

**Severity:** High
**Status:** Open
**Component:** `DocumentService::getLastIdOfPath()` (`src/Services/DocumentService.php`), reached via `POST`/`GET /apis/{site}/api/patient/{pid}/document`

**Summary:** Omitting the required `path` query parameter doesn't produce a clean `400` — it returns `200 OK` with a raw HTML error page as the body, including full absolute server file paths through several internal PHP files.

**Repro:**
1. `POST /api/patient/{pid}/document` (or the equivalent `GET`) with no `path` query parameter, but a valid file part.
2. Response is `200 OK`. Body contains an HTML block with the text `Query Error` and absolute paths through `_rest_routes.inc.php`, `DocumentRestController.php`, `DocumentService.php`, and `dispatch.php`.

**Root cause:** `getLastIdOfPath()` runs a parameterized `SELECT id FROM categories WHERE ... = ?` query with a `null` bound value when `path` is absent. MariaDB rejects this at prepare time, and the resulting fatal error is caught only by the global DB error handler, which echoes an HTML error block without ever calling `http_response_code()` to override the default `200`.

**Impact:** Information disclosure (server-side file layout) delivered with a status code that looks like success — a caller checking only the status code would treat this as a normal response, not an error.

**Automated coverage:** `Post_Document_Missing_Path_Query_Param_Returns_OkWithRawSqlErrorBody` and `Get_Document_List_Missing_Path_Query_Param_Returns_OkWithRawSqlErrorBody` in `DocumentApiTests.cs`.

---

## 3. Message `PUT`/`DELETE` report success on zero rows matched (COMPLETED)

**Severity:** High
**Status:** Open
**Component:** `MessageService::update()`/`delete()` (`src/Services/MessageService.php`)

**Summary:** The inverse of finding #1 — both `PUT` and `DELETE` on this resource return `200` regardless of whether the `WHERE` clause actually matched a row, including for a `mid` that never existed at all. A caller has no way to distinguish "it worked" from "it silently did nothing."

**Repro:**
1. `PUT /api/patient/{pid}/message/999999999` (a `mid` that was never created) → `200 OK` with `{"mid": "999999999"}`.
2. `DELETE /api/patient/{pid}/message/999999999` → `200 OK`, no error.
3. `DELETE /api/patient/{otherPid}/message/{realMid}` (a real message addressed through the wrong patient's pid, which the SQL *does* correctly refuse to touch) → still `200 OK`; a direct DB check shows `pnotes.deleted` is still `0`.

**Root cause:** Both methods return whatever `sqlStatement()` hands back — a truthy prepared-statement handle regardless of affected-row count — not a row-count check. `RestControllerHelper::responseHandler()` has no falsy signal to turn into a `404`.

**Impact:** Silent-failure reporting. Any integration relying on the response status to confirm a write/delete actually happened will be systematically wrong.

**Automated coverage:** `Put_Message_For_Nonexistent_Mid_Still_Returns_Ok`, `Delete_Message_With_Mismatched_Patient_Id_Returns_OkButDoesNotDelete`, `Delete_Message_For_Nonexistent_Mid_Still_Returns_Ok` in `MessageApiTests.cs`.

---

## 4. `PUT /api/patient/{puuid}/insurance/{uuid}` is a full destructive overwrite (COMPLETED)

**Severity:** High
**Status:** Open
**Component:** `InsuranceService::update()` (`src/Services/InsuranceService.php`)

**Summary:** A `PUT` to this endpoint isn't a partial update — every column not included in the request body is overwritten with `null`, silently destroying previously-stored data.

**Repro:**
1. Create a Patient Insurance record with a full set of subscriber fields populated.
2. `PUT` the same record with only `{"policy_number": "..."}`.
3. `GET` the record again — `policy_number` is updated as expected, but `subscriber_lname`, `subscriber_fname`, `provider`, `date`, and every other previously-set field is now `null`.

**Root cause:** `InsuranceRestController::put()` passes the raw request body straight to `InsuranceService::update()`, which runs a single `UPDATE insurance_data SET <every column> = ? ... WHERE uuid = ?` using `$data`'s values as-is.

**Impact:** Real PHI data-loss risk for any caller who assumes `PUT` semantics match every other resource on this API (a partial patch of only the fields sent) — Facility's `PUT`, for comparison, only touches fields actually included in the payload.

**Automated coverage:** `Update_PatientInsurance_With_Partial_Payload_Nulls_Unset_Fields` in `PatientInsuranceApiTests.cs`.

---

## 5. `GET /api/procedure` crashes the entire endpoint on one bad row (COMPLETED)

**Severity:** High
**Status:** Open
**Component:** `ProcedureService::getAll()`/`getOne()` (`src/Services/ProcedureService.php`)

**Summary:** Both the list and single-record `GET` routes for Procedure return a bare `500` — for every caller, not just the affected record — whenever any `procedure_order` row's `encounter_id` or `provider_id` doesn't resolve to a real `form_encounter`/`users` row. A procedure order that hasn't yet been tied to a real visit, or whose ordering provider was later deleted, is an entirely plausible real-world state, not a contrived edge case.

**Repro:**
1. Insert a `procedure_order` row with an `encounter_id` that doesn't match any real `form_encounter.id`.
2. `GET /api/procedure` (the whole list, not just this record) → bare `500`, no JSON body.

**Root cause:** Both methods manually call `UuidRegistry::uuidToString($row['euuid'])` (and `pruuid`) on the result of a `LEFT JOIN` to `form_encounter`/`users`, with no null guard — unlike `BaseService::createResultRecordFromDatabaseResult()` (used by Immunization/Prescription), which wraps the same conversion in an `isset()` check. Confirmed via the Apache error log: an uncaught `TypeError` from `Ramsey\Uuid\Uuid::fromBytes()`.

**Impact:** Availability — a single orphaned row poisons the entire list endpoint for every caller, not just a query for that record.

**Automated coverage:** `Get_Procedure_With_Unresolved_Encounter_Returns_InternalServerError` in `ProcedureApiTests.cs`.

---

## 6. `GET /api/prescription/{uuid}` is unconditionally broken (COMPLETED)

**Severity:** Medium
**Status:** Open
**Component:** `PrescriptionService::getOne()` (`src/Services/PrescriptionService.php`)

**Summary:** The single-record read for this resource returns `500` on every call — valid uuid or not. There is no working single-record read path for Prescription at all; only the unfiltered list route returns usable data.

**Repro:** `GET /api/prescription/{any-uuid}` → `500`, body is a raw MariaDB "SQL Statement failed on preparation" error.

**Root cause:** `getOne($uuid)` calls `$this->getAll(['_id' => $uuid], $puuidBind)`, but the `combined_prescriptions` query has no column named `_id` (the real identifying column is `uuid`) — the search-field builder generates `WHERE (BINARY _id = ?)`, which MariaDB rejects outright.

**Impact:** Availability of a documented read path, though the list endpoint remains a usable workaround.

**Automated coverage:** `Get_Prescription_By_Uuid_Always_Returns_InternalServerError` in `PrescriptionApiTests.cs`.

---

## 7. `POST`/`PUT /api/insurance_company` always `500` (COMPLETED)

**Severity:** Medium
**Status:** Open
**Component:** `InsuranceCompanyRestController::post()`/`put()` (`src/RestControllers/InsuranceCompanyRestController.php`)

**Summary:** Both write routes for this resource call a method that doesn't exist anywhere in the class hierarchy. The resource is documented as `crus` but in practice behaves as `rs` on this OpenEMR version.

**Repro:** `POST /api/insurance_company` with a valid payload → `500`. Apache error log: `PHP Fatal error: Uncaught Error: Call to undefined method OpenEMR\Services\InsuranceCompanyService::validate()`.

**Root cause:** `InsuranceCompanyService` (and its parent `BaseService`) genuinely has no `validate()` method in this OpenEMR version — a real source-level bug, not a payload or scope problem.

**Impact:** This resource cannot be created or updated through the REST API at all; `GET` (list/get-by-id) is unaffected.

**Automated coverage:** `Create_InsuranceCompany_Returns_InternalServerError`, `Update_InsuranceCompany_Returns_InternalServerError` in `InsuranceCompanyApiTests.cs`.

---

## 8. Zero DB triggers exist anywhere in the schema — no audit trail on a direct write (COMPLETED)

**Severity:** Medium
**Status:** Open (by design in this OpenEMR version, not a bug to fix in this project)
**Component:** Database schema-wide

**Summary:** Every audit/log write (the `log` table) is entirely PHP-application-mediated (`EventAuditLogger::newEvent()`). A raw SQL write to `patient_data` that bypasses the OpenEMR application layer entirely produces **no audit trail whatsoever** — a real, HIPAA-relevant gap, not a contrived edge case, since any direct-DB migration, admin script, or future bug that writes to a clinical table outside the app silently produces an unaudited change.

**Repro:** Insert a patient row directly via SQL, then query `log` for that `patient_id` — zero rows, confirmed directly.

**Root cause:** `information_schema.triggers` returns 0 rows for the whole schema — there is no DB-level enforcement mechanism at all for this; it relies entirely on every code path going through the correct PHP service layer.

**Impact:** Any write that doesn't go through the application (a data migration, a direct admin fix, a future bug) is invisible to compliance/audit review.

**Automated coverage:** `No_Database_Triggers_Exist_Anywhere_In_This_Schema`, `Direct_Sql_Insert_Into_Patient_Data_Produces_No_Audit_Log_Row_Then_Rolled_Back` in `AuditLogDbTests.cs`.

---

## 9. Document upload with a human-readable category silently uploads an unfindable file (COMPLETED)

**Severity:** Medium
**Status:** Open
**Component:** `DocumentService::getLastIdOfPath()`/`Document::createDocument()`

**Summary:** Uploading with a human-readable category path (e.g. `Medical Record`, matching what the OpenEMR UI displays) returns `200`/`true` and genuinely creates a downloadable file — but the document can never appear in any list, under any path, including the exact string used at upload, because the category-link row was never written.

**Repro:**
1. `POST /api/patient/{pid}/document?path=Medical%20Record` with a valid file → `200`/`true`.
2. `GET /api/patient/{pid}/document?path=Medical%20Record` (the identical string) → `404`.
3. The file itself does exist on disk and in the `documents` table — it's simply unreachable via any list call.

**Root cause:** `getLastIdOfPath()`'s SQL compares `replace(LOWER(name), ' ', '')` (the DB column, transformed) against the raw bound `$path` parameter (only `_` is stripped — no lowercasing, no space-stripping), so a human-readable path never matches and returns a `null` category id. `Document::createDocument()`'s `is_numeric($category_id)` guard then silently skips the `categories_to_documents` insert.

**Impact:** Real-world data loss risk from the caller's perspective (a file that exists but can never be found again through the API) despite every individual call reporting success.

**Automated coverage:** `Post_Document_With_HumanReadable_Category_Name_Uploads_But_Is_Never_Listable` in `DocumentApiTests.cs`.

---

## 10. Patient-nested Allergy `GET` routes always return empty (COMPLETED)

**Severity:** Low
**Status:** Open (workaround exists)
**Component:** `AllergyIntoleranceService::getAll()` (`src/Services/AllergyIntoleranceService.php`)

**Summary:** `GET /api/patient/{puuid}/allergy` and `GET /api/patient/{puuid}/allergy/{auuid}` always return an empty result, even for a patient with real allergy records — despite being the routes documented for exactly this purpose.

**Repro:** Create an allergy for a patient, then immediately list it through the patient-nested route → `{"data": []}`. The identical record is visible through `GET /api/allergy?puuid={puuid}` (the top-level route with a query filter).

**Root cause:** The nested routes call `getAll(['lists.pid' => $puuid])`, which gets remapped to `$search['patient_id'] = $puuid` and compared as a raw string (`StringSearchField`) against the numeric `lists.patient_id` column — it can never match. Only the top-level route's `puuid` search key goes through the correct UUID-to-pid resolution (`TokenSearchField`).

**Impact:** Low severity because a working equivalent exists (the top-level route with a `puuid` filter) — but the documented, "obvious" route for this purpose is silently non-functional, and `POST`/`DELETE` on the same nested routes work fine, which makes the read-side failure easy to miss.

**Automated coverage:** `Get_Allergy_List_By_Patient_Nested_Route_Returns_Empty_Despite_Existing_Record`, `Get_Allergy_List_Filtered_By_Puuid_Query_Param_Returns_Created_Record` in `AllergyApiTests.cs`.

---

## 11. Neither insurance↔patient nor billing↔encounter has a DB-level foreign key (COMPLETED)

**Severity:** Low
**Status:** Open (architectural characteristic of this OpenEMR version, not something this project can fix)
**Component:** Database schema — `insurance_data`, `billing`, `form_encounter`

**Summary:** Both tables are InnoDB (which supports foreign keys), but no FK constraints are declared. A direct SQL insert with a nonexistent `pid` or `encounter` is accepted without complaint — referential integrity here is a PHP-application convention only, never something the database itself enforces.

**Repro:** Insert an `insurance_data` row (or a `billing` row) referencing a `pid`/`encounter` that doesn't exist in `patient_data`/`form_encounter` — the insert succeeds with no error.

**Root cause:** `information_schema.KEY_COLUMN_USAGE` returns zero rows for both tables' foreign-key relationships, confirmed directly.

**Impact:** Low severity under normal operation (the application layer is consistent about this), but any future direct-DB script or migration bug can silently introduce orphaned billing/insurance records with no database-level safety net.

**Automated coverage:** `Insurance_Data_And_Billing_Have_No_Declared_Foreign_Key_Constraints`, the `Direct_Insert_*_Is_Flagged_By_The_Orphan_Check` pairs in `ReferentialIntegrityDbTests.cs`; the equivalent `form_encounter` version in `FormEncounterDbTests.cs`.

---

## 12. `billing.encounter` can't reference the encounter values this project's own fixtures generate (COMPLETED)

**Severity:** Low
**Status:** Open (latent schema mismatch)
**Component:** Database schema — `billing.encounter` (`int(11)`) vs. `form_encounter.encounter` (`bigint(20)`)

**Summary:** This project's own Encounter API test fixtures assign `encounter` values sized from `DateTime.UtcNow.Ticks` (18 digits), which already exceed what `billing.encounter`'s `int(11)` type can hold (~2.1 billion). A billing row genuinely cannot reference such an encounter — inserting one overflows with a MariaDB "Out of range value" error rather than a graceful validation failure.

**Repro:** Attempt to insert a `billing` row with `encounter` set to a real, large `form_encounter.encounter` value from this environment (e.g. `639202645064751777`) — MariaDB rejects it as out of range for `int(11)`.

**Root cause:** A column-width mismatch between the two tables that predates this project — not something introduced by test fixtures, just exposed by them.

**Impact:** Low severity in isolation, but worth knowing before ever wiring real billing-to-encounter linkage through this schema in a future session.

**Automated coverage:** Documented directly in `CONTEXT.md`'s Known Constraints; no dedicated test, since reproducing it would require an intentionally-overflowing insert with no useful assertion beyond "MariaDB rejects it."

---

## 13. FHIR was silently disabled by a one-word env var typo (resolved) (COMPLETED)

**Severity:** Was blocking all FHIR coverage; not a current risk
**Status:** Resolved
**Component:** `docker/docker-compose.yml`, `globals.gl_value` (`rest_fhir_api`), OAuth scope casing

**Summary:** Included even though it's fixed, because "found and fixed a masked root cause with two independent, stacked layers" is a strong story on its own. `docker-compose.yml` set `OPENEMR_SETTING_fhir_api: 1`, but the real global OpenEMR checks is named `rest_fhir_api` — a one-word typo meant this instance's FHIR toggle had silently defaulted to off since the container was first created, with no error surfaced anywhere because `OPENEMR_SETTING_<anything>` env vars are applied unconditionally regardless of whether the name matches a real global. Fixing that alone exposed a **second**, independent bug: FHIR resource scopes are case-sensitively distinct from legacy REST scopes of the same resource name (`user/patient.read` vs. `user/Patient.read` are separate, independently-grantable entries), and the test scope string only had the lowercase form.

**Root cause:** Two stacked, independent misconfigurations — an env var name typo, and a missing capitalized OAuth scope — each fully masking the other until fixed in the right order.

**Impact:** All FHIR coverage was blocked for the life of the project until both layers were found and fixed; now confirmed to survive a genuinely fresh container (`docker compose down -v && up`), not just the already-patched instance.

**Automated coverage:** `Fhir_Patient_Search_Returns_Valid_Bundle`, `Fhir_Appointment_Search_Returns_Valid_Bundle`, and the 5 newer FHIR resource tests added across `EncounterApiTests.cs`, `AllergyApiTests.cs`, `ConditionApiTests.cs`, `MedicationRequestApiTests.cs`, `ObservationApiTests.cs`.

---

## 14. Calendar day-view single-click-to-edit throws a JS error for "No Show"-category appointments (resolved on the test side; real app bug remains) (COMPLETED)

**Severity:** Medium
**Status:** Open in OpenEMR itself; the corresponding UI test is fixed
**Component:** `event_time_click()`/`EditEvent()` (`interface/main/calendar/modules/PostCalendar/pntemplates/default/views/day/ajax_template.html` and `header.html`)

**Summary:** Single-clicking the "Click to edit" time link on a day-view appointment whose category type renders with the `event_noshow` CSS class (as opposed to `event_appointment`) throws an uncaught `TypeError: Cannot read properties of undefined (reading 'id')` in the browser and never opens the edit dialog at all. This is the actual root cause of what was previously tracked as "canceling an appointment does not remove it from the day view" — the delete button was never reachable, not broken itself.

**Repro (live, confirmed via a raw Playwright script, not just the test suite):**
1. Book any appointment without changing the default Category dropdown (which defaults to category id `1`, "No Show", on this instance's seed data).
2. On the day view, single-click the appointment's `11:00`-style time link.
3. A page-level `pageerror` fires (`Cannot read properties of undefined (reading 'id')`); `page.frame({ url: /add_edit_event\.php/ })` never resolves — no dialog opens.
4. The same appointment opens fine via **double-click** anywhere on its day-view block.

**Root cause:** `event_time_click(elem)` calls `EditEvent($(elem).parents("div.event_appointment").get(0))`. That jQuery `.parents()` selector is hardcoded to the `event_appointment` class only. For a "No Show"-categorized event, the day view instead wraps the link in `<div class="event_noshow event">` — no ancestor matches `div.event_appointment`, `.get(0)` returns `undefined`, and `EditEvent(undefined)` immediately dereferences `eObj.id`, throwing before `oldEvt()`/`dlgopen()` is ever reached. The separate `.dblclick()` handler is bound directly to `div.event` (matching both `event_appointment` and `event_noshow`) and passes `this` straight into `EditEvent()`, bypassing the broken lookup entirely — which is why double-click always worked and single-click never did for this category.

**Impact:** Any appointment left in a category whose day-view class isn't literally `event_appointment` (confirmed for "No Show"; not exhaustively tested against the other 14 categories in `openemr_postcalendar_categories`) can never be reopened via the documented single-click "Click to edit" affordance — a real front-desk user would hit this, not just this test suite.

**Test-side fix:** `CalendarPage.openExistingAppointment()` now double-clicks the appointment's wrapping `div.event` (matched by patient-identifying text, not by time — see below) instead of single-clicking the inner `a.event_time` link, sidestepping the app bug the way double-click already does. Two compounding test bugs were fixed alongside it: (1) `existingAppointmentLink`/`openExistingAppointment` previously matched `.first()` of all elements at a given clock time with no patient scoping, so on a non-fresh environment with multiple stale same-time fixture appointments (a known, separately-tracked gap — see `TEST-PLAN.md`'s test-data-lifecycle item), it could silently open and act on the wrong appointment entirely; it now matches on the specific patient's last name instead. (2) `deleteCurrentEvent()`/`openExistingAppointment()` had no wait for the edit dialog's iframe to actually finish loading after the click before looking up `#form_delete`, so `frame?.$('#form_delete')` could resolve `undefined` and `button?.click()` would silently no-op via optional chaining — the same "operation silently did nothing" shape already documented elsewhere in this project (see finding #3). A short poll for the iframe to attach was added, matching the existing `confirmDuplicateCheck()` polling pattern in `PatientRegistrationPage.ts`.

**Automated coverage:** `patient-scheduling.spec.ts`'s `canceling an appointment removes it from the day view`, now reliably green; confirmed via a direct DB check (not just the UI assertion) that the row is actually removed from `openemr_postcalendar_events`.

---

## 15. Login page's language `<select>` has no accessible name (COMPLETED)

**Severity:** Low
**Status:** Open
**Component:** `interface/login/login.php`

**Summary:** The language-picker dropdown on the login screen (`<select class="form-control" name="languageChoice" size="1">`) has no `id`, no associated `<label>`, and no `aria-label`/`aria-labelledby` — a screen reader announces it only as "combo box," with no indication of what it selects. Confirmed live via `@axe-core/playwright` (`select-name`, WCAG 4.1.2, `critical` impact) against the actual rendered login page, not the treeLine baseline (which never crawled login at all — the crawl session started pre-authenticated).

**Repro:** Load `/interface/login/login.php?site=default` and run an axe-core scan — `select-name` is the only `critical`-impact violation on the page.

**Impact:** A screen-reader user attempting to change the interface language before logging in has no way to know which control does that.

**Automated coverage:** `accessibility.spec.ts`'s `login page has exactly one known critical violation: the language select has no accessible name` — asserts this exact, current violation set rather than a should-be-clean ideal, consistent with this project's convention of documenting real behavior directly (see finding #1).

---

## 16. Patient registration form's collapsible sections have invalid `aria-controls` values (COMPLETED)

**Severity:** Low
**Status:** Open
**Component:** `interface/new/new.php` (Employer/Stats/Misc/Guardian/Insurance collapsible panels)

**Summary:** Each collapsible section header button (`Employer`, `Stats`, `Misc`, `Guardian`, `Insurance`, and others) sets `aria-controls` to a bare numeric or short id (e.g. `aria-controls="4"`, `aria-controls="ins"`) that doesn't match any real element in the DOM — the actual panel id is `div_4`/`div_ins`. Confirmed live via `@axe-core/playwright` (`aria-valid-attr-value`, WCAG 4.1.2, `critical` impact, 8 affected buttons) against the real patient-registration iframe (`iframe[src$="new.php"]`); this page was never part of the treeLine crawl baseline either.

**Repro:** Log in, open Patient > New/Search, and run an axe-core scan scoped to the page (including the `pat` iframe) — all 8 critical violations are this one rule, one per collapsible section toggle button.

**Impact:** A screen reader announces these expand/collapse buttons without any indication of which content region they control, since the `aria-controls` reference is broken — the visual/mouse experience is unaffected since the actual `data-target`-driven Bootstrap collapse behavior still works.

**Automated coverage:** `accessibility.spec.ts`'s `patient registration form has exactly one known critical violation: invalid aria-controls on the collapsible sections`.

---

## 17. Every FHIR search Bundle's `meta.lastUpdated` violates the official FHIR R4 JSON schema (COMPLETED)

**Severity:** Medium
**Status:** Open
**Component:** `FhirResourcesService::createBundle()` (`src/Services/FHIR/FhirResourcesService.php`)

**Summary:** The FHIR `instant` datatype (used by `Bundle.meta.lastUpdated`) requires a full ISO 8601 datetime with an explicit timezone offset or `Z` suffix — the official `fhir.schema.json`'s pattern for it hard-requires this. Every FHIR search response from this OpenEMR version sets the *Bundle's own* `meta.lastUpdated` to a bare, timezone-less local timestamp instead (e.g. `"2026-07-27T16:06:52"`), which fails that pattern. This is systemic, not a one-off: validating a real search Bundle for all 7 FHIR resources this project covers (Patient, Appointment, Encounter, AllergyIntolerance, Condition, MedicationRequest, Observation) against the official schema turns up exactly this one violation, every time, and no others.

**Repro:** `GET /fhir/{any resource}` and inspect the root `meta.lastUpdated` — it's always `Y-m-d\TH:i:s` with no offset. Compare to any individual entry's own `resource.meta.lastUpdated` (e.g. `entry[0].resource.meta.lastUpdated`), which *is* correctly formatted (`"2026-07-21T23:49:48+00:00"`) — the defect is isolated to the Bundle envelope's own metadata, not the resources it contains.

**Root cause:** Confirmed by reading source directly: `FhirResourcesService::createBundle()` builds the Bundle's `meta` with `$nowDate = date("Y-m-d\TH:i:s"); $meta = array('lastUpdated' => $nowDate);` — plain PHP `date()`, which has no timezone-offset format specifier here at all. Every per-resource FHIR service, by contrast, builds its *own* resource-level `meta.lastUpdated` via `UtilsService::getDateFormattedAsUTC()` (`(new \DateTime())->format(DATE_ATOM)`) or an equivalent `DATE_ATOM`-formatted call, which does include the offset — so the underlying FHIR resource-conversion code already knows how to do this correctly, this one Bundle-envelope call site just doesn't reuse it.

**Impact:** Any real integration or FHIR client that validates responses against the official R4 JSON schema before accepting them — a normal, expected practice for interoperability testing — would reject every single search result this server returns, regardless of resource type. A bare `resourceType === "Bundle"` spot-check (the assertion this project's own FHIR tests used before this session) can never catch this, since the field is present and superficially date-shaped; only real schema validation surfaces it.

**Automated coverage:** `FhirSchemaValidator.ValidateBundleAllowingKnownLastUpdatedDefect()` (`tests/OpenEmr.Api.Tests/Fhir/FhirSchemaValidator.cs`), used by all 7 `Fhir_*_Search` tests across `PatientApiTests.cs`, `AppointmentApiTests.cs`, `EncounterApiTests.cs`, `AllergyApiTests.cs`, `ConditionApiTests.cs`, `MedicationRequestApiTests.cs`, and `ObservationApiTests.cs` — validates the full response against the real, official `fhir.schema.json` (vendored at `tests/OpenEmr.Api.Tests/Fhir/Schemas/fhir.schema.json`) and asserts zero violations *other than* this one documented, path-excluded defect, so the tests stay green until it's fixed or a genuinely new schema regression appears (same pattern as `FINDINGS.md` #1 and the accessibility findings above). The 4 tests that already isolate one specific fixture-created entry (Encounter, AllergyIntolerance, Condition, MedicationRequest) additionally validate that entry's `resource` object against its own resource-type schema definition (e.g. `"Encounter"`), confirmed to pass cleanly — the per-resource FHIR conversion itself is schema-compliant; only the shared Bundle-wrapping code has this defect.
