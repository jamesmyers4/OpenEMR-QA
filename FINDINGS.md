# FINDINGS.md

Standalone write-ups for the most serious, source-confirmed defects found while building this test suite — pulled out of `CONTEXT.md`'s Known Constraints, where they were competing for attention with routine "here's how this endpoint actually behaves" notes. Every finding here was confirmed against the live instance (a real `curl`/API call, a direct DB query, or both) and root-caused by reading the actual OpenEMR PHP source, not guessed at from documentation. Findings #1–17 are ordered by severity, most serious first, from the initial build-out. Findings #18+ come from the later, deliberate "grey area" concurrency/reliability phase (see `CONTEXT.md`'s Purpose & Vision) and are appended in the order they were found rather than resequenced into the severity ordering above, to avoid invalidating every existing `FINDINGS.md #N` cross-reference elsewhere in this repo — each entry's own `Severity` field remains the authoritative signal, not its position in the list. Each finding names the automated test that demonstrates it, where one exists.

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

**Automated coverage:** `Billing_Row_Cannot_Reference_A_Realistic_Sized_Form_Encounter_Encounter_Value_Then_Rolled_Back` in `FormEncounterDbTests.cs` — inserts a `form_encounter` row with a realistic `DateTime.UtcNow.Ticks`-sized `encounter` value (matching this project's existing Encounter fixture pattern), asserts the subsequent `billing` insert throws a `MySqlException` containing "Out of range value," then rolls back. Documents the current broken state as an explicit, checked assertion rather than prose, so a future schema migration that widens `billing.encounter` fails this test in an informative way instead of drifting silently.

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

---

## 18. Concurrent `POST /api/patient` races on an unguarded `MAX(pid)+1` read and silently drops most requests while reporting `200` (COMPLETED)

**Severity:** High
**Status:** Open
**Component:** `PatientService::insert()` (`src/Services/PatientService.php`), reached via `POST /apis/{site}/api/patient`

**Summary:** Under genuine concurrent traffic — not a contrived edge case, just more than one request in flight at once — most `POST /api/patient` calls silently fail while still reporting `200 OK`. This is the same false-success/info-disclosure shape already documented in finding #2 (Document upload with a missing `path`), now confirmed on Patient itself, and triggered by ordinary concurrent load rather than a malformed request.

**Repro:** Fire 15 concurrent `POST /api/patient` requests, each with a distinct, individually valid payload, at the same instant. Confirmed reproducible across multiple live trials: consistently only 3–5 of 15 return `201 Created`; the rest return `200 OK` with a raw HTML "Query Error" body containing `Duplicate entry '<n>' for key 'pid'`, plus full absolute server file paths through `PatientService.php`, `PatientRestController.php`, `_rest_routes.inc.php`, `HttpRestRouteHandler.php`, and `dispatch.php`.

**Root cause:** `PatientService::insert()` computes the new row's `pid` via a plain `SELECT MAX(pid)+1 FROM patient_data`, with no `SELECT ... FOR UPDATE`, advisory lock, or retry, before its own `INSERT` (confirmed by reading source; the stack trace in the failure body itself points at `sqlInsert` and `databaseInsert` in `PatientService.php`). Two concurrent requests that both read the table before either commits its insert compute the identical next `pid`. The database genuinely does have a real, unique index on `pid` (confirmed via `SHOW INDEX FROM patient_data`, `Non_unique = 0`) and correctly rejects the second, colliding `INSERT` — but the resulting `MySqlException` is only caught by the framework's global DB error handler, which echoes an HTML error block without ever calling `http_response_code()`, so the default `200` status stands. This is the identical defect class already known from finding #2, now confirmed on the system's single most foundational resource and confirmed to be triggered by realistic concurrent volume, not just a caller omitting a parameter.

**Impact:** A caller checking only the HTTP status code — the normal, reasonable thing to do — cannot distinguish "patient created" from "patient silently not created" under concurrent load. Multiple front-desk staff registering walk-ins at the same moment, a bulk-import integration, or a client retrying a slow request can all lose a majority of the intended patient records while every single response looks like success, with the failure responses additionally leaking full server file-system paths.

**Automated coverage:** `Concurrent_Patient_Creates_Race_On_Computed_Pid_And_Report_False_Success_On_Collision` (`ConcurrencyApiTests.cs`) fires 15 concurrent creates and asserts both halves of this behavior at once: at least one false-`200` collision occurs, at least one clean `201` success occurs, and no two successfully-created patients ever share a `pid` (the DB-level unique index does hold even though the app-level error handling doesn't). A companion DB-layer test, `Concurrent_Raw_Inserts_Reusing_The_Same_Computed_Next_Pid_Are_Rejected_By_A_Real_Unique_Index` (`ConcurrencyDbTests.cs`), isolates and confirms the unique-index protection exists at the database layer independent of any PHP-level serialization, by deliberately forcing two raw connections to read the same stale `MAX(pid)+1` before either inserts.

---

## 19. Concurrent writes to the same Message/Appointment record extend the known "reports success regardless of what happened" pattern to genuine race conditions (COMPLETED)

**Severity:** Medium
**Status:** Open
**Component:** `MessageService::update()` (`src/Services/MessageService.php`), `AppointmentService::deleteAppointmentRecord()` (`src/Services/AppointmentService.php`)

**Summary:** Findings #1 and #3 already established that Message's `PUT`/`DELETE` routes report `200` regardless of whether a write actually matched a row, demonstrated with sequential calls against mismatched or nonexistent ids. Under genuine concurrent load against the *same*, real id, the identical missing-locking pattern produces two further, distinct failure modes: (1) concurrent `PUT`s to one message id lose some updates outright — a classic read-modify-write lost update, since `MessageService::update()` reads the existing body and writes `existing + new` with no locking around that cycle — rather than every concurrent edit being durably applied; (2) concurrent `DELETE`s of the same appointment id all report `200 "record deleted"` even though only the first request could possibly have deleted anything.

**Repro:** (1) Create one message, fire 10 concurrent `PUT` requests each appending a distinct marker string, then read the final `body` directly from `pnotes`. Confirmed across multiple live trials: only 6–7 of the 10 markers survive each time, with no error or partial-failure indication in any of the 10 HTTP responses (all return `200`). (2) Create one appointment, fire 5 concurrent `DELETE` requests at the same `eid` — all 5 return `200 {"message":"record deleted"}` even though the row can only physically be deleted once.

**Root cause:** Same as findings #1/#3 — both `MessageService::update()` and `AppointmentService::deleteAppointmentRecord()` report success based on a truthy statement handle / the absence of a thrown exception, not an actual affected-row count, and neither takes any lock around its read-then-write (Message) or check-then-delete (Appointment) cycle. The existing findings already established the *sequential* symptom (editing through the wrong pid, deleting a nonexistent id); this entry documents that the same root cause also produces a genuine, silent lost-update race under real concurrent traffic against a legitimate, existing record — arguably a more likely real-world trigger (two staff editing the same note at once, a slow UI double-submitting a cancel) than a caller deliberately supplying a wrong or nonexistent id.

**Impact:** Two concurrent edits to the same clinical note can silently lose one edit's content with no indication to either caller that anything was dropped. A doubled cancel/delete action reports identical success whether it was the first, real delete or a redundant no-op — low-risk for Appointment in isolation (the end state, "the appointment is gone," is the same either way), but it's the same silent-failure-reporting shape that is genuinely dangerous elsewhere in this API (see finding #3's wrong-pid `DELETE` case, where the redundant call's silent no-op is exactly the dangerous part).

**Automated coverage:** `Concurrent_Puts_To_Same_Message_Lose_Updates_Under_Race` and `Concurrent_Deletes_Of_Same_Appointment_All_Report_Success_Though_Only_One_Row_Existed`, both in `ConcurrencyApiTests.cs`.

---

## 20. Concurrent `PUT`s to the same Patient Insurance record let the last writer silently erase every other concurrent update, not just its own unset fields (COMPLETED)

**Severity:** High
**Status:** Open
**Component:** `InsuranceService::update()` (`src/Services/InsuranceService.php`)

**Summary:** Finding #4 already established that a single `PUT` to this endpoint is a full, unconditional column overwrite — any field not included in that one request's payload is nulled. Under genuine concurrent traffic this amplifies into a materially more dangerous multi-user failure mode: when two or more callers each `PUT` a different single field of the *same* insurance record at the same time, every field update except whichever request's write physically commits last is silently discarded — including fields that a different, already-completed concurrent request had just successfully written moments earlier. Every caller involved receives an identical `200` success response.

**Repro:** Create a Patient Insurance record with a full set of subscriber fields populated. Fire 5 concurrent `PUT` requests at the same uuid, each setting exactly one different field (`policy_number`, `subscriber_lname`, `subscriber_fname`, `subscriber_city`, `subscriber_state`) to a distinct, recognizable value. All 5 responses return `200`. `GET` the record afterward: only one of the five fields — whichever request's `UPDATE` physically committed last — retains its new value; the other four requests' updates are gone with no error or warning anywhere, exactly as if those calls had never been made. Confirmed live via both raw concurrent `curl` and the automated test below, reproducible every time.

**Root cause:** Same as finding #4 — `InsuranceRestController::put()` passes the raw request body straight to `InsuranceService::update()`, which runs a single `UPDATE insurance_data SET <every column> = ? ... WHERE uuid = ?` using only that one request's `$data` values, with no locking, no optimistic-concurrency check (no version/timestamp comparison), and no partial-column update logic. Finding #4 demonstrated the danger from a single caller's own perspective (a caller who doesn't realize `PUT` isn't a partial patch loses their own previously-set data). This finding demonstrates the same root cause is materially worse across concurrent callers: it is not merely "my own omitted fields get nulled," it is "any other caller's concurrent, already-applied write to this same record can be silently annihilated by my own write, and vice versa," with neither caller ever informed.

**Impact:** A real multi-user data-loss risk in exactly the kind of workflow a live clinic actually has — billing staff updating a policy number while front-desk staff updates a subscriber's address for the same patient at the same time. Whichever request's database write happens to land last erases the other's change completely, and both staff members see an identical "success" response with no indication anything was lost. This is a more severe, concurrency-amplified instance of finding #4's already-documented destructive-overwrite defect.

**Automated coverage:** `Concurrent_Puts_To_Same_PatientInsurance_Record_Let_The_Last_Writer_Erase_Every_Other_Concurrent_Update` (`ConcurrencyApiTests.cs`) fires 5 concurrent single-field `PUT`s and asserts exactly one of the five distinct field values survives in the final record.

---

## 21. Concurrent Practitioner creates with an identical username all succeed — `users.username` has no uniqueness guard at any layer (COMPLETED)

**Severity:** Medium
**Status:** Open
**Component:** `PractitionerService::insert()` (`src/Services/PractitionerService.php`), backed by `users.username`

**Summary:** Unlike the Patient `pid` race (finding #18), which does have a real DB-level guard that the app layer merely mishandles, there is no protection at all here against duplicate usernames. Firing several concurrent `POST /api/practitioner` requests with the identical `username` produces exactly that many real, independently-addressable practitioner accounts, all sharing one username, with zero rejection at any layer.

**Repro:** Fire 5 concurrent `POST /api/practitioner` requests, each with a distinct `fname`/`lname`/`npi` but the identical `username`. All 5 return `201 Created` with distinct real ids/uuids. `GET /api/practitioner` afterward lists all 5, all sharing the identical username, with no indication anything unusual happened. Confirmed reproducible every single time — unlike the Patient/Message/Insurance races, this isn't a narrow timing window that sometimes loses, it always succeeds for every request, since nothing anywhere in the stack can reject a colliding value.

**Root cause:** `users.username` has no unique index at the database level (confirmed via `SHOW INDEX FROM users`, already a documented known constraint and covered passively by `UsersDbTests.cs`'s `Users_Table_Has_No_Duplicate_Populated_Usernames`/`Users_Table_Has_No_Unique_Constraint_On_Username`/`Direct_Insert_Duplicate_Username_Is_Accepted_By_Schema_Then_Rolled_Back`), and neither `PractitionerValidator` nor `PractitionerService::insert()` perform any application-level uniqueness check either — confirmed by reading source, only `fname`, `lname`, and `npi` are validated as required; `username` is never checked against existing rows.

**Impact:** A real account-identity-confusion risk in a workflow OpenEMR itself makes plausible — two admin operators provisioning new provider accounts around the same time, or a provisioning script re-run without first checking for an existing record, can silently create multiple, indistinguishable-by-username login accounts. Since `AuthUtils.php`'s login lookup (per `UsersDbTests.cs`'s existing `Direct_Insert_Disabled_User_Is_Excluded_By_Login_Active_Predicate_Then_Rolled_Back` test) matches on username via `WHERE BINARY username = ?`, a genuine duplicate resolves to whichever row the query happens to return first — a real login-identity ambiguity, not just a data-hygiene nitpick.

**Automated coverage:** `Concurrent_Practitioner_Creates_With_Identical_Username_All_Succeed_With_No_Uniqueness_Guard` (`ConcurrencyApiTests.cs`) fires 5 concurrent creates with a shared username and asserts all 5 succeed with distinct ids, all remaining independently visible in the practitioner list under that username. The test cleans up its own fixture rows in a `finally` block (the same pattern already established by `ProcedureApiTests`'s intentionally-orphaned-row cleanup) — leaving them behind would permanently poison `UsersDbTests.Users_Table_Has_No_Duplicate_Populated_Usernames`, a real collision this test's own first draft caused live during this session before the cleanup was added.

---

## 22. Concurrent duplicate-patient merge submissions for the same pair are never rejected — no locking exists around the destructive merge/delete loop (COMPLETED)

**Severity:** High
**Status:** Open
**Component:** `merge_patients.php` (`interface/patient_file/merge_patients.php`)

**Summary:** This already-known, hardcoded-`$PRODUCTION = true` destructive merge operation (deleting the source patient's `patient_data`/`history_data`/`insurance_data` rows and reassigning every other table's references to the target) has no locking, mutex, or idempotency guard of any kind around its per-table delete/update loop. Firing two genuinely concurrent submissions of the identical merge — the same target/source pid pair, same CSRF token, same form data, exactly what a double-click on "Merge" or two admin tabs confirming the same pending merge would produce — results in *both* requests independently passing the initial existence checks, both racing through the same `SHOW TABLES` loop, and both reporting `200`/"Merge complete." Even though both report success, the actual set of `DELETE`/`UPDATE` statements each one physically executed is a non-deterministic, non-overlapping split of the total merge work.

**Repro:** Create two patients. Load the merge confirmation screen once to obtain a valid CSRF token (`CsrfUtils`'s tokens are stateless HMACs derived from the session's private key, not single-use, so the same token is valid for reuse across concurrent submissions). Fire two concurrent `POST`s to `merge_patients.php` with identical `form_target_pid`/`form_source_pid`/`csrf_token_form`/`form_submit=merge` payloads. Confirmed live across multiple runs: both responses return `200` and both contain "Merge complete.", but each response's own echoed operation log (the page inlines `echo "<br />$sql ($count)"` for every statement it personally executes) shows a *different* subset of the real work — e.g. one run had request A perform the `patient_data`+`history_data` deletes while request B performed the `insurance_data` delete; a second run split it differently again. A subsequent, sequential third submission for the same pair correctly returns "Source patient not found," confirming the source row genuinely was deleted exactly once with no crash in these runs — but that held by luck of the specific tables' simple full-row-delete semantics, not because of any actual protection.

**Root cause:** `merge_patients.php` wraps none of its work in a database transaction or any application-level lock, confirmed by reading source — every `deleteRows()`/`updateRows()`/`mergeRows()` call is an independent, unguarded `SELECT COUNT(*) ...; if ($count) { DELETE/UPDATE ... }` pair with no lock between the count and the write. Two concurrent requests executing this loop for the same source/target pid each independently observe row counts and act on them, non-deterministically splitting the actual database work between the two PHP worker processes depending on how their statement execution happens to interleave.

**Impact:** A real availability/data-integrity risk for a highly destructive, irreversible operation this project's own source comments already warn to back up the database before using. A double-click or a second admin tab confirming an already-in-flight merge is entirely plausible, not a contrived edge case. In these live runs the simple tables involved (full-row deletes keyed by `pid`) happened to split safely with no double-deletion errors — but `mergeRows()`'s more complex, per-row, type-matching delete/update logic (used for `lists_touch`) reads and conditionally deletes/updates individual rows by comparing dates between source and target, a materially riskier pattern under the same kind of concurrent interleaving that was not exhaustively probed here (no `lists_touch` fixture data was set up for either patient in this test) — a plausible avenue for genuine data loss under the right table/row combination, flagged as an open follow-up rather than guessed at further.

**Automated coverage:** `two concurrent merge submissions for the same pair both report success despite no locking guard` (`ui/tests/admin-duplicate-merge-race.spec.ts`) fires the two concurrent `POST`s via Playwright's `context.request` (sharing the authenticated admin session's cookies), asserts both report `200`/"Merge complete.", then fires a third, sequential `POST` for the same pair and asserts it correctly reports "Source patient not found" — proving the underlying deletion did complete exactly once despite neither of the two concurrent requests detecting the other's simultaneous submission.

---

## 23. Switching back to a top-nav tab whose content URL hasn't changed can silently fail to make it visible again on Firefox (COMPLETED)

**Severity:** Medium
**Status:** Open
**Component:** `navigateTab()` / `menuActionClick()` (`interface/main/tabs/js/tabs_view_model.js`)

**Summary:** Clicking a top-nav tab (e.g. "Calendar") that already has an iframe loaded with the exact URL the click would navigate it to again can silently leave that tab hidden — the click registers, the app's own click handler runs without error, but the tab's wrapping container never becomes visible. Confirmed to be a genuine Firefox-vs-Chromium behavioral difference, not a test-authoring bug: the identical scenario works correctly on Chromium.

**Repro:** Log in (Calendar is this instance's default landing tab, so its iframe loads real content immediately on login), navigate away to another tab (e.g. Patient), then click "Calendar" again in the top nav without having changed anything that would alter the calendar view's own URL (same day, same provider filter). On Firefox, the tab never becomes visible again — `div.frameDisplay` wrapping `iframe[name="cal"]` stays `display:none` indefinitely. On Chromium, the identical sequence works and the tab becomes visible immediately. Confirmed via direct instrumentation: attaching a native `load` event listener to the iframe before the click shows the event fires on Chromium but never fires on Firefox within several seconds, for the exact same click and the exact same (unchanged) target URL.

**Root cause:** Confirmed by reading `tabs_view_model.js` directly. `menuActionClick(data, evt)` calls `navigateTab(url, name, callback)` once `data.enabled()` is confirmed true (ruled out as the cause here — confirmed live via `ko.dataFor()` that `enabled()` is `true` throughout this entire scenario, so the click handler's main branch does run). `navigateTab()`, when the named iframe already exists, binds `$("iframe[name='"+name+"']").one('load', function () { afterLoadFunction(); })` — where `afterLoadFunction` is what actually calls `activateTabByName(name, true)`, the *only* code path that sets the tab's Knockout `visible` observable to `true` — and then immediately sets `iframe.get(0).contentWindow.location = url`. If `url` is identical to what the iframe's `contentWindow` already has loaded, Firefox does not fire a new `load` event for that reassignment (confirmed live via `elementFromPoint`/computed-style checks showing the link and surrounding DOM are otherwise completely healthy — correct `display`/`visibility`/`opacity`, correct position, no occlusion — the failure is purely that the visibility-toggling callback never runs, not that anything else is broken), so the bound `.one('load', ...)` handler simply never fires and `activateTabByName` never runs. Chromium, by contrast, does fire `load` for the same same-URL `contentWindow.location` reassignment, so the identical code path works there. Neither browser is "wrong" per any HTML/DOM spec requirement here — same-URL navigation refiring `load` is implementation-defined — but `navigateTab()`'s design assumes it always will, which doesn't hold for at least one of the two browsers this project targets.

**Impact:** A real, if narrow, usability defect for actual Firefox users of this application, not just an automated-test artifact: any workflow where a user switches away from a tab and back to it without changing that tab's own view parameters (a very plausible thing to do — e.g. checking a patient's chart, then clicking back to Calendar to see the same day view they were already on) can result in the top nav appearing to do nothing at all, with no error or feedback of any kind. This is the root cause that was blocking `patient-portal.spec.ts` on Firefox across three separate investigation sessions before being traced to source.

**Automated coverage:** No dedicated test isolates this defect directly (it would require asserting on `div.frameDisplay`'s computed style specifically, which no test currently does as a first-class assertion). It's exercised indirectly by `patient-portal.spec.ts`'s Calendar tab-switch step, which now works reliably thanks to a test-side mitigation: `CalendarPage.goto()` (`ui/pages/CalendarPage.ts`) checks whether `iframe[name="cal"]` actually became visible after the click and, if not, calls the app's own `activateTabByName('cal', true)` function directly via `page.evaluate()` — the same "invoke the real JS callback directly" pattern already established elsewhere in this project for legacy dialog handling — rather than waiting on a `load` event that this defect confirms will never come on Firefox. This fix is verified to resolve the tab-visibility symptom specifically (confirmed via repeated direct checks), though `patient-portal.spec.ts` itself still fails downstream for an entirely separate, still-open reason — see the follow-up flagged in `ROADMAP.md`.

---

## 24. Concurrent Facility creates with identical name and NPI all succeed — no uniqueness guard at any layer (COMPLETED)

**Severity:** Medium
**Status:** Open
**Component:** `FacilityService::insert()` (`src/Services/FacilityService.php`), backed by the `facility` table

**Summary:** The same unguarded shape already confirmed for Practitioner's `username` (finding #21), now confirmed for Facility too. Firing several concurrent `POST /api/facility` requests with the identical `name` and `facility_npi` produces exactly that many real, independently-addressable facility rows, all sharing both fields, with zero rejection at any layer.

**Repro:** Fire 5 concurrent `POST /api/facility` requests, all with the identical `name` and `facility_npi`. All 5 return `201 Created` with distinct real ids/uuids. `GET /api/facility` afterward lists all 5, all sharing the identical name and NPI, with no indication anything unusual happened. Confirmed reproducible every single time — like Practitioner's username race and unlike the Patient/Message/Insurance races, this isn't a narrow timing window that sometimes loses; it always succeeds for every request, since nothing anywhere in the stack can reject a colliding value.

**Root cause:** Confirmed via `SHOW CREATE TABLE facility`: the only constraints are the `id` `PRIMARY KEY` (a genuine `AUTO_INCREMENT` column, not an application-computed value like `patient_data.pid`) and a `UNIQUE KEY` on `uuid` (always distinct, since each insert generates its own real `UuidRegistry` uuid). There is no unique index on `name`, `facility_npi`, or any other column. Reading `FacilityValidator::configureValidator()` confirms the application layer doesn't fill the gap either — the insert context only requires `name` (length 2-255) and `facility_npi` (numeric, length 10-15) to be present and well-formed; neither this validator nor `FacilityService::insert()` ever checks a submitted value against existing rows.

**Impact:** A real duplicate-facility-record risk in a plausible administrative workflow — two admins provisioning a new location around the same time, or a provisioning script re-run without first checking for an existing record, can silently create multiple, field-for-field-identical facility rows. Since Facility records feed billing/claims location data and provider-facility associations elsewhere in the app, an operator picking "the" facility from a dropdown with several indistinguishable duplicates has no way to know which one is correct.

**Automated coverage:** `Concurrent_Facility_Creates_With_Identical_Name_And_Npi_All_Succeed_With_No_Uniqueness_Guard` (`ConcurrencyApiTests.cs`) fires 5 concurrent creates with a shared name and NPI and asserts all 5 succeed with distinct ids and uuids, all remaining independently visible in the facility list under that name. The test cleans up its own fixture rows in a `finally` block, the same convention already established by the Practitioner race test.

---

## 25. A single failed merge can partially cannibalize the source patient — no transaction wraps `merge_patients.php`'s per-table loop, and any one table's constraint violation kills the whole operation mid-migration (COMPLETED)

**Severity:** High
**Status:** Open
**Component:** `merge_patients.php` (`interface/patient_file/merge_patients.php`), `sqlStatement()`/`HelpfulDie()` (`library/sql.inc.php`)

**Summary:** `FINDINGS.md` #22 already established that `merge_patients.php`'s per-table delete/update loop has no surrounding transaction, in the context of two *concurrent* merges racing each other. This is the same missing-transaction defect's consequence for a *single* merge request: the loop walks every table returned by `SHOW TABLES` (alphabetical on this MariaDB version) and, for any table with a `pid`/`patient_id` column not otherwise special-cased, fires a plain unconditional `UPDATE ... SET pid = target WHERE pid = source`. If that `UPDATE` fails for any reason on any one table — a real, plausible unique-constraint collision, not a contrived fault injection — `sqlStatement()` routes the failure to `HelpfulDie()`, which prints a raw "Query Error" page and calls PHP's bare `exit`. Since each table's statement was already its own auto-committed write with nothing to roll it back, every table processed before the failure point is left migrated to the target patient, while every table after it — including `patient_data` itself, which is what actually makes the source patient "gone" on a successful merge — is never touched at all.

**Repro:** Create two patients, target and source. Seed one `patient_access_onsite` row for each (a genuinely ordinary real-world state — one portal-access row per patient). `patient_access_onsite` has a single-column `UNIQUE KEY` on `pid` alone, so the loop's attempt to re-point the source's row (`UPDATE patient_access_onsite SET pid = target WHERE pid = source`) collides with the target's own pre-existing row and fails. Also seed a `lists` (Condition) row for the source patient (sorts alphabetically before `patient_access_onsite` in `SHOW TABLES`) and a `pnotes` (Message) row for the source patient (sorts alphabetically after). Submit one ordinary, non-concurrent merge `POST` with a valid CSRF token. Confirmed live: the response is `200` (not a clean error status — same "raw SQL error page reported as a normal response" pattern as `FINDINGS.md` #2/#9) containing the raw "Query Error" page, not "Merge complete." Querying the database directly afterward: the `lists` row's `pid` is now the target's — that table's write committed before the crash. The `pnotes` row's `pid` is still the source's — that table was never reached. `patient_access_onsite` still has both original rows, unchanged. `patient_data` still has a full row for the source patient — its own deletion, alphabetically later in the loop than `patient_access_onsite`, never ran.

**Root cause:** Two compounding, already-individually-known behaviors meeting on a single request: (1) no transaction wraps `merge_patients.php`'s per-table loop (`FINDINGS.md` #22), so each table's write is independently, permanently committed the instant it succeeds; (2) `sqlStatement()`'s failure path (`HelpfulDie()`) is a bare `exit`, not a caught-and-continued or caught-and-rolled-back error, so one failing statement anywhere in the loop halts the entire remaining operation instantly with no cleanup of any kind.

**Impact:** A materially worse outcome than the already-known concurrency race (#22), and reachable by a single ordinary request with no timing or double-submission required — just one colliding row in any of the dozens of tables this loop blindly walks. The result isn't a clean failure or a clean success; it's a patient record split across two identities: the source patient still exists as its own full record (so nothing alerts an operator that anything is wrong — the "duplicate" is still right there in the patient list), while an unknown, table-order-dependent subset of its clinical/administrative data has already been silently and irreversibly reassigned to the target patient. Given this loop touches every `pid`-bearing table in the schema — not a short, reviewed list — any future table added to OpenEMR with a `pid` column and any kind of secondary uniqueness constraint becomes a new, silent trigger for this same failure mode.

**Automated coverage:** `a merge that hits a unique-constraint collision partway through the per-table loop dies mid-migration, leaving some tables migrated and others untouched` (`ui/tests/admin-duplicate-merge-partial-failure.spec.ts`) seeds the exact precondition above, submits one ordinary merge request, and asserts the response contains "Query Error" (not "Merge complete."), that the `lists` row migrated, that the `pnotes` row did not, that both `patient_access_onsite` rows remain unchanged, and that the source patient's own `patient_data` row still exists.
