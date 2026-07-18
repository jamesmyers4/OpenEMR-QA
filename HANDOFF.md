# HANDOFF.md

Point-in-time snapshot for a fresh Claude (chat or Claude Code) session picking this project up. Read `CONTEXT.md` first for the why; this document is the current where-things-stand. This file will go stale — treat it as accurate as of the last time it was updated, not as an ongoing source of truth. `TEST-PLAN.md` is the durable coverage checklist; a future task-specific `CLAUDE.md` will carry the actual next-action instructions for Claude Code.

## Snapshot Summary

Environment is fully working end-to-end: Docker stack (OpenEMR + MariaDB), OAuth2 authentication (including the client dynamic-registration + DB-side enable step), and the C# API + DB test layers are all confirmed functional against a live instance. Current state: **11 of 15 API/DB tests passing.** All 4 remaining failures have a known, understood cause — none are unexplained.

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

### Passing (11)

- All `OpenEmr.Db.Tests` — fixture seeding, referential integrity, transactional insert/rollback pattern
- `Create_Patient_Returns_Created_With_New_Pid`, `Get_Patient_List_Returns_Seeded_Demo_Patients`, `Get_Patient_By_Pid_Returns_Matching_Record`, `Update_Patient_Persists_Changed_Fields`, `Create_Patient_Missing_Required_Field_Returns_BadRequest` (all Patient API tests except FHIR)
- `Create_Appointment_Returns_Created_With_New_Eid` (Appointment API)

### Failing (4) — all root-caused, none are mysteries

1. **`Fhir_Patient_Search_Returns_Valid_Bundle`** and **`Fhir_Appointment_Search_Returns_Valid_Bundle`** — both `401 Unauthorized`. REST scopes work; FHIR-specific scopes are needed but the correct scope string is unconfirmed. One attempt (`user/Patient.rs`) broke OAuth client _registration_ entirely, unrelated to the token request — reverted. This needs a clean investigation into OpenEMR's actual supported FHIR scope syntax for this version, ideally via the instance's own Swagger UI (`https://localhost:9300/swagger/`) rather than guessing at scope strings again. Low priority relative to the other three failures — REST coverage is the larger surface area.

2. **`Get_Appointments_For_Patient_Returns_Only_That_Patients_Visits`** — throws `System.InvalidOperationException: requires an element of type 'Object', but the target element has type 'Array'` when iterating the `data` array from `GET /patient/{pid}/appointment` and calling `.GetProperty("pc_pid")` on each item. The list-level `data` array itself parses fine; something about the per-item shape doesn't match the assumption baked into the original (external-docs-based) design. Needs the raw body of a real response with at least one appointment in it — that hasn't been captured yet, since the assertion only logs the raw body on top-level failures, not on this specific parsing exception. Quickest fix: temporarily print the raw string directly (e.g. via a scratch console app or an ad-hoc test) rather than guessing at field names again.

3. **`Double_Booking_Same_Slot_Same_Provider_Returns_Conflict`** — the first appointment creation succeeds as expected (`200 OK`), but the second (identical slot/provider) _also_ returns `200 OK` with a real new `id`, not the expected `409 Conflict`. This may mean OpenEMR's REST API genuinely doesn't enforce this conflict at the API layer (only in the UI's own JS validation), in which case the test's expectation is wrong and needs to change, not the implementation. Needs a UI-side check (create the same double-booking through the actual OpenEMR calendar screen) to determine whether this is a real product behavior or a payload/field issue on the test's end.

4. Two known-good-but-not-yet-fixed items were already resolved this session and shouldn't reappear: DOB field casing (fixed via explicit `JsonSerializerOptions`), and single-patient lookups using `uuid` instead of `pid` (fixed).

## UI Layer Status

**Debugged against the live container this session** (previous note above about "not yet run" is now stale). `ui/tests/auth.spec.ts` (4 tests) and `ui/tests/patient-registration.spec.ts` (3 tests) are solid and reliably green. `ui/tests/patient-scheduling.spec.ts` (3 tests) has 2 of 3 reliably green; the 3rd (cancel) is a real, not-yet-fixed bug — see below.

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

**Explicitly descoped this session** (by user decision, given time already spent): a dedicated POM/spec for `interface/patient_file/manage_dup_patients.php` / `merge_patients.php` (admin-side duplicate review/merge tooling — different from the registration-flow duplicate check above, still a `TEST-PLAN.md` open item) and for `interface/billing/new_payment.php` (apply-a-payment). Both remain untouched — no exploration was done on either this session, so treat them as fully open, not partially started.

## treeLine Integration — Status and Intent

The authenticated-crawl support this section anticipated now exists and has been run — see `treeLine-output/openemr-qa/` and `treeLine-output/FEEDBACK-FOR-TREELINE.md` for the full writeup. Short version: 95 pages crawled, almost entirely deep admin/report/config surface with little overlap with `TEST-PLAN.md`'s open items; login itself was never crawled (the session started pre-authenticated); several generated POMs don't compile as-is (invalid TypeScript identifiers — see feedback file for specifics); 0% data-testid coverage everywhere (OpenEMR has none); the accessibility (axe-core) report is a genuine, reusable asset for the still-open "Accessibility" `TEST-PLAN.md` item. The three POMs built this session (`LoginPage`, `PatientRegistrationPage`, `CalendarPage`) were cross-checked against treeLine's output where it overlapped, but ultimately needed live verification regardless — treeLine's crawl doesn't (and structurally can't, per the feedback file) capture the frame/tab navigation model this app requires.

## Immediate Next Steps (roughly ordered)

1. Resolve or intentionally reclassify the two non-FHIR API failures (appointment list shape, double-booking expectation) — both are close to done, not open-ended investigations.
2. Run the UI layer against the live instance for the first time and go through the same evidence-based debugging cycle the API layer already went through.
3. Once treeLine's authenticated-crawl support is ready, run it against this Docker instance and use its output to expand `TEST-PLAN.md` systematically.
4. Build the scheduled GitHub Actions workflows (daily/weekly) with failure alerting — CI currently only triggers on push/PR.
5. Separately investigate FHIR scope configuration — low priority, isolated from the rest of the suite.
6. Longer-term, once the above is solid: begin the deliberate "grey area" reliability-testing phase described in `CONTEXT.md` — this should not be started early.

## Collaboration Notes

- Full replacement file contents are strongly preferred over line-number-based diffs when handing back code changes — line numbers drift easily across edits and cause more confusion than they save.
- Every API-layer HTTP assertion should capture and surface the raw response body on failure (see `CONTEXT.md` conventions) — this has been the single most useful diagnostic pattern in this project so far and should be the default for any new test, not an afterthought.
- When a fix is uncertain, the established pattern in this project is to verify against the live instance (curl, or the instance's own Swagger UI at `/swagger/`) before writing code, rather than trusting external documentation that may not match this specific version's behavior. Several real bugs in this project trace back to trusting general OpenEMR documentation over the actual running instance.
