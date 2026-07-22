# HANDOFF.md

Point-in-time snapshot for a fresh Claude (chat or Claude Code) session picking this project up. Read `CONTEXT.md` first for the why; this document is the current where-things-stand. This file will go stale — treat it as accurate as of the last time it was updated, not as an ongoing source of truth. `TEST-PLAN.md` is the durable coverage checklist; a future task-specific `CLAUDE.md` will carry the actual next-action instructions for Claude Code.

## Snapshot Summary

Updated 2026-07-21 (Session 1 of `REVIEW-PASS-AND-FHIR-EXPANSION-SESSION.md`). Environment is fully working end-to-end: Docker stack (OpenEMR + MariaDB), OAuth2 authentication (including the client dynamic-registration + DB-side enable step), FHIR access, and the C# API + DB test layers are all confirmed functional against a **genuinely fresh** instance, not just a long-lived one. Current state: **99 of 99 API/DB tests passing** (75 `OpenEmr.Api.Tests`, 24 `OpenEmr.Db.Tests`), re-verified this session against a container rebuilt from `docker compose down -v && up -d` — see `CONTEXT.md`'s Known Constraints for the fresh-container FHIR confirmation and two DB-test findings (one fixed, one open) discovered along the way.

One real, unresolved gap found this session: `AuditLogDbTests.Application_Mediated_Patient_Inserts_Are_Represented_In_The_Audit_Log` races against `OpenEmr.Api.Tests` (the two test projects run as concurrent processes under `dotnet test OpenEmr.Tests.sln`) and can fail on the very first run against a zero-history database — passes reliably afterward. Not yet fixed; see `CONTEXT.md` and `REVIEW-PASS-AND-FHIR-EXPANSION-SESSION.md` Part A5 for the two candidate fixes.

The UI layer (Playwright/TypeScript) is scaffolded but has not yet been run against the live instance beyond one confirmed selector fix. It has not been debugged the way the API/DB layers have.

## Environment — How To Stand It Up

```
cd docker
docker compose up -d
docker compose ps
```

Wait for both `mariadb` and `openemr` to show `healthy` — first boot after a volume reset takes several minutes (schema install + Apache startup), not seconds.

**First-login one-time steps on a fresh volume** (via browser, `https://localhost:9300`, `admin`/`pass`):

1. Product registration prompt — email optional/blank is fine, telemetry opt-in is a personal choice, no effect on tests
2. **Administration → Config → Connectors** — confirm these are checked:
   - Enable OpenEMR Standard REST API
   - Enable OpenEMR Standard FHIR REST API (enabled, but FHIR access still doesn't work — see Known Issues)
   - Site Address set to `https://localhost:9300`
   - Whether "Enable OAuth2 Password Grant" needs to be explicitly toggled on, versus the tests working regardless, has not been isolated — it was set during troubleshooting but never tested in isolation with it off. Treat as needed until proven otherwise.

Then:

```
cd ../tests
dotnet restore OpenEmr.Tests.sln
dotnet test OpenEmr.Tests.sln
```

## Current Test Status

### Passing (19)

- All `OpenEmr.Db.Tests` (5) — fixture seeding, referential integrity, transactional insert/rollback pattern
- `Create_Patient_Returns_Created_With_New_Pid`, `Get_Patient_List_Returns_Seeded_Demo_Patients`, `Get_Patient_By_Pid_Returns_Matching_Record`, `Update_Patient_Persists_Changed_Fields`, `Create_Patient_Missing_Required_Field_Returns_BadRequest` (all Patient API tests except FHIR)
- `Create_Appointment_Returns_Created_With_New_Eid`, `Get_Appointments_For_Patient_Returns_Only_That_Patients_Visits`, `Double_Booking_Same_Slot_Same_Provider_Is_Allowed_With_Distinct_Ids` (all Appointment API tests except FHIR)
- `Create_Encounter_Returns_Created_With_New_Euuid`, `Get_Encounters_For_Patient_Returns_Only_That_Patients_Encounters`, `Close_Encounter_Sets_Last_Level_Closed_True` (all Encounter API tests, new this session)
- `Get_Practitioner_List_Returns_Practitioner_With_Username`, `Get_Practitioner_By_Uuid_Returns_Matching_Record`, `Create_Practitioner_Without_Username_Is_Invisible_To_List_And_Get` (all Practitioner API tests, new this session)

### Failing (2) — one root cause, not a mystery

1. **`Fhir_Patient_Search_Returns_Valid_Bundle`** and **`Fhir_Appointment_Search_Returns_Valid_Bundle`** — now both `501 Not Implemented` with an empty body (confirmed via direct `curl` against `GET /apis/default/fhir/Patient` with a valid bearer token — this is a status-code change from the previously-documented `401 Unauthorized`, not a re-investigation of the same failure). A `501` from the app itself suggests the FHIR route genuinely isn't wired up in this environment, which is a different diagnosis than "wrong OAuth scope string" — worth a fresh look before assuming the old scope-string theory still applies. Ideally investigate via the instance's own Swagger UI (`https://localhost:9300/swagger/`) rather than guessing again. Still low priority relative to REST coverage; still explicitly deferred, not attempted this session.

**Resolved in a prior session** (previously 2 of the 4 known failures, fixed in `AppointmentApiTests.cs`):

- **Appointment list per-item shape** — root-caused via a raw `curl` GET against `/apis/default/api/patient/{pid}/appointment`: the response root **is the array itself** (`[{...}, {...}]`), not an object with a `data` property. The original test's `.GetProperty("data")` call on an array-typed root element was the exact source of the `InvalidOperationException`. Fixed by parsing the root as an array directly and asserting every item's `pid` matches the test patient, which is what the test's name always claimed to check but never actually did.
- **Double-booking "conflict"** — confirmed via direct `curl` (two identical POSTs to the same patient/slot/provider) that OpenEMR's REST API returns `200 OK` with a new distinct `id` both times; there is no server-side conflict enforcement. This matches the UI-layer finding (soft `confirm()` warning, not a hard block — see UI Layer Status below). The test was renamed to `Double_Booking_Same_Slot_Same_Provider_Is_Allowed_With_Distinct_Ids` and now asserts the real, confirmed product behavior instead of an assumption that was never true.

**New this session — Encounter and Practitioner resource coverage** (`EncounterApiTests.cs`, `PractitionerApiTests.cs`), following `TEST-PLAN.md`'s suggested build order of finishing Layer 1 (API) resource-by-resource. All shapes verified against the live container via direct `curl` and by reading the relevant `RestController`/`Service`/`Validator` PHP source before writing any C# — see `CONTEXT.md`'s Known Constraints for the full detail on each finding below:

- Encounter's patient route parameter is the patient **UUID** (`puuid`), not `pid` — different from Appointment's `pid`-based routes. `EncounterApiTests` creates its fixture patient and captures `uuid` (not `pid`) for this reason.
- Encounter's list endpoint wraps its array in a `data` property, unlike Appointment's bare-array list response — another per-resource enveloping inconsistency, not a pattern to assume carries over.
- Closing/signing an encounter is done via `PUT /api/patient/{puuid}/encounter/{euuid}` with `last_level_closed: "1"` — there's no dedicated close/sign action route. This `PUT` call requires the caller to explicitly pass `user` (a valid username) and `group` (any string) in the body; unlike `POST`, the controller does not inject these from the session automatically, and omitting them returns a `400` that isn't documented in the OpenAPI comments.
- Practitioners created via `POST /api/practitioner` without a `username` field are permanently invisible to both `GET /api/practitioner` and `GET /api/practitioner/{uuid}` — confirmed root cause in `PractitionerService::search()`, which filters out any `users` row with a null/empty username by default. `Create_Practitioner_Without_Username_Is_Invisible_To_List_And_Get` asserts this real (if surprising) behavior directly rather than treating it as a bug to route around silently.
- `appsettings.test.json`'s OAuth `Scope` string now also requests `user/practitioner.read`/`user/practitioner.write`, needed for the new Practitioner tests.

## UI Layer Status

**Debugged against the live container this session** (previous note above about "not yet run" is now stale). `ui/tests/auth.spec.ts` (4 tests) and `ui/tests/patient-registration.spec.ts` (3 tests) are solid and reliably green. `ui/tests/patient-scheduling.spec.ts` (3 tests) has 2 of 3 reliably green; the 3rd (cancel) is a real, not-yet-fixed bug — see below. `ui/tests/billing-payment.spec.ts` (1 test, new this session) is green.

**New this session — Billing apply-a-payment (`EncounterPage.ts`, `BillingPaymentPage.ts`, `billing-payment.spec.ts`)**, closing the `TEST-PLAN.md` "apply a payment" item flagged as descoped in the treeLine buildout session. Built from treeLine's crawl of `interface/billing/new_payment.php` as a starting point, but two live-verification findings meant the treeLine output couldn't be used as-is:

- **The real top-nav "Fees > Payment" menu item does not go to `new_payment.php`** (the URL treeLine actually crawled) — it navigates to a different, undocumented-in-treeLine page, `interface/patient_file/front_payment.php`. Confirmed by clicking through the real top-nav menu rather than trusting the crawled URL; another instance of treeLine's direct-URL crawl missing the app's real navigation graph, consistent with `FEEDBACK-FOR-TREELINE.md`'s point about frame/tab navigation not being captured.
- **`Fees > Payment` is disabled in the top nav until a patient is selected**, and even with a patient selected, `front_payment.php` requires that patient to have an encounter/visit already created for today — submitting a payment against a patient with zero encounters returns "Sorry No Appointment is Fixed. No Encounter could be created." instead of a payment receipt. `EncounterPage.createVisit()` (Patient > Visits > Create Visit, `interface/forms/newpatient/new.php`) creates that visit first; it also required discovering a client-side validation rule not visible without reading the page's inline JS (`pc_catid` must not stay on its default placeholder value `_blank` — `validationConstraints: {"pc_catid":{"exclusion":["_blank"]}}`), which the treeLine POM for this page didn't surface at all.
- On success, `front_payment.php` reloads in place with a "Receipt for Payment" view (How Paid / Amount for This Visit / Received By, plus Print/Delete/Exit buttons) rather than redirecting — the spec asserts on that receipt text and the itemized amount cell.

**Real bugs found and fixed in existing scaffolding:**
- `LoginPage.ts`'s submit button selector was `#login_button` (underscore) — the real rendered DOM uses `#login-button` (hyphen). Confirmed via live DOM inspection; this was a plain typo, not a treeLine/environment issue.
- The "OpenEMR Product Registration" modal appears after **every** login in this container (not just first-ever boot) and blocks all subsequent interaction until dismissed. `LoginPage.loginAs()` now dismisses it via "Ask again later" automatically — any new spec that logs in gets this for free.
- `auth.spec.ts`'s original assertions were wrong in three places: successful login lands on `interface/main/tabs/main.php?token_main=...`, not `main_screen.php`; the failed-login error indicator is the text "Invalid username or password", not `#loginfailure`; logout is a knockout-bound dropdown (`#username[data-toggle="dropdown"]` → click "Logout" text inside `#userdropdown`), not `a[href*="logout.php"]`. All three were guesses that never matched the real app.

**Architectural discovery that matters for every future POM on this app:** OpenEMR's authenticated UI is a tabbed frameset (`main.php`). The default tabs (Calendar, Message Center) load into iframes with **stable, hand-assigned names** — `iframe[name="cal"]`, `iframe[name="msg"]` — confirmed via `page.evaluate(() => Array.from(document.querySelectorAll('iframe')).map(f => ({name: f.name, src: f.getAttribute('src')})))`. Opening a new tab (e.g. Patient → New/Search) assigns another stable name (`iframe[name="pat"]`). **Never call `page.goto()` a second time mid-session** (with or without `?auth=login`) — it reliably produces a page reading "Authentication Error"; the only reliable way to switch tabs is clicking the real top-nav menu (`.menuLabel` divs, `data-toggle="dropdown"` on the ones with submenus) the way a user would. `PatientRegistrationPage.goto()` and `CalendarPage.goto()` both do this now.

**New POMs/specs built this session, both frame-navigation-aware:**
- `ui/pages/PatientRegistrationPage.ts` + `ui/tests/patient-registration.spec.ts` — new-patient happy path, required-field validation (asserts the real inline `#error_form_lname` / `.error-border` behavior, not a native alert — OpenEMR's newer `validate.js`-based form doesn't alert on this page), and the duplicate-check review step. **Real finding:** OpenEMR's new-patient form always runs a name+DOB match search before creating (even with zero matches) and shows a review popup — this *is* the "duplicate patient detection" TEST-PLAN item, just discovered via the registration flow rather than a separate admin page.
- `ui/pages/CalendarPage.ts` + `ui/tests/patient-scheduling.spec.ts` — booking an appointment (uses the real `setpatient(pid, lname, fname, dob)` JS function the app itself calls after its patient-search popup closes, rather than fighting that popup directly), and double-booking. **Real finding, corrects the TEST-PLAN checkbox label:** double-booking the same provider slot is a **soft warning**, not a hard block — `confirm("Provider not available, use it anyway?")`; the user can proceed anyway. This is also directly relevant to the open API-layer question in this file about `Double_Booking_Same_Slot_Same_Provider_Returns_Conflict` — the UI doesn't enforce a hard conflict either, so that API test's *expectation* (not the implementation) is very likely the thing to fix.

**Known, real, unresolved bug — next agent should start here:** "canceling an appointment removes it from the day view" fails consistently. `CalendarPage.deleteCurrentEvent()` clicks `#form_delete` inside the event frame, the native `confirm("Deleting this event cannot be undone...")` dialog is accepted (handled by the shared `page.on('dialog')` in the spec's `beforeEach`), but the appointment is still present in the day view afterward — not a timing issue, confirmed via direct DB check (`SELECT * FROM openemr_postcalendar_events` still shows the row). Next step: read `deleteEvent()`/`SubmitForm()` in the container's `interface/main/calendar/add_edit_event.php` (`docker exec docker-openemr-1 grep -n "function deleteEvent" -A 40 /var/www/localhost/htdocs/openemr/interface/main/calendar/add_edit_event.php`, `MSYS_NO_PATHCONV=1` prefix needed in Git Bash to stop path-mangling) — likely needs the same `top.restoreSession()` call the save flow needed, or the delete AJAX call needs a different completion signal than what `save()` uses.

**Known, real, but intermittent flakiness — root-caused, not a mystery, mitigated not eliminated:** the duplicate-check popup (`new_search_popup.php`, patient-registration) and the calendar's own `dlgopen()`-based dialogs are built on a legacy jQuery dialog library (`library/dialog.js`) that dynamically loads `interactjs` and other assets before becoming interactive. Confirmed empirically that **identical interaction code succeeds reliably when driven by a bare Playwright script** (`chromium.launch()` directly, no test runner) **but is measurably less reliable when driven through `npx playwright test`** — tried and ruled out: `--trace=off`, disabling video, `page.bringToFront()`, longer explicit waits, clicking via `ElementHandle` instead of `Locator`. Never found the actual root cause of that specific runner-vs-script gap; if picking this up again, that's the one real open mystery in the UI layer. Current mitigation: `PatientRegistrationPage.confirmDuplicateCheck()` invokes the popup's own callback directly (`document.forms[0].submit()` + `top.restoreSession()`) instead of clicking its button, plus explicit `.dialogModal`/`.modal-backdrop` DOM cleanup; both new spec files set `test.describe.configure({ retries: 2-3 })` as a backstop. This gets both specs green in practice but isn't a real fix.

**Explicitly descoped, still open:** a dedicated POM/spec for `interface/patient_file/manage_dup_patients.php` / `merge_patients.php` (admin-side duplicate review/merge tooling — different from the registration-flow duplicate check above, not its own `TEST-PLAN.md` line item but real queued work). No exploration has been done on this one yet — treat it as fully open, not partially started. (Billing apply-a-payment, previously listed here as also descoped, is now covered — see above.)

## treeLine Integration — Status and Intent

The authenticated-crawl support this section anticipated now exists and has been run — see `treeLine-output/openemr-qa/` and `treeLine-output/FEEDBACK-FOR-TREELINE.md` for the full writeup. Short version: 95 pages crawled, almost entirely deep admin/report/config surface with little overlap with `TEST-PLAN.md`'s open items; login itself was never crawled (the session started pre-authenticated); several generated POMs don't compile as-is (invalid TypeScript identifiers — see feedback file for specifics); 0% data-testid coverage everywhere (OpenEMR has none); the accessibility (axe-core) report is a genuine, reusable asset for the still-open "Accessibility" `TEST-PLAN.md` item. The three POMs built this session (`LoginPage`, `PatientRegistrationPage`, `CalendarPage`) were cross-checked against treeLine's output where it overlapped, but ultimately needed live verification regardless — treeLine's crawl doesn't (and structurally can't, per the feedback file) capture the frame/tab navigation model this app requires.

## Immediate Next Steps (roughly ordered)

1. Root-cause the cancel-appointment UI bug — `CalendarPage.deleteCurrentEvent()` doesn't remove the row from `openemr_postcalendar_events`, see UI Layer Status above for the specific next debugging step.
2. Build the scheduled GitHub Actions workflows (daily/weekly) with failure alerting — CI currently only triggers on push/PR.
3. Investigate the FHIR `501 Not Implemented` — now a status-code change from the previously-documented `401`, worth a fresh look rather than resuming the old scope-string theory. Still low priority relative to REST coverage.
4. Continue expanding `TEST-PLAN.md`'s open API items resource-by-resource — Encounter and Practitioner are now done; Facility, Insurance, Allergy, Immunization, Procedure/Prescription, Document, and Message are still open, plus Appointment update/reschedule/cancel/recurring. On the UI side, Billing apply-a-payment is now done (this session) — reschedule/drag-drop, clinical encounter, billing claim generation, RBAC, and the admin duplicate-merge tooling (`manage_dup_patients.php`/`merge_patients.php`) remain untouched.
5. Longer-term, once the above is solid: begin the deliberate "grey area" reliability-testing phase described in `CONTEXT.md` — this should not be started early.

## Collaboration Notes

- Full replacement file contents are strongly preferred over line-number-based diffs when handing back code changes — line numbers drift easily across edits and cause more confusion than they save.
- Every API-layer HTTP assertion should capture and surface the raw response body on failure (see `CONTEXT.md` conventions) — this has been the single most useful diagnostic pattern in this project so far and should be the default for any new test, not an afterthought.
- When a fix is uncertain, the established pattern in this project is to verify against the live instance (curl, or the instance's own Swagger UI at `/swagger/`) before writing code, rather than trusting external documentation that may not match this specific version's behavior. Several real bugs in this project trace back to trusting general OpenEMR documentation over the actual running instance.
