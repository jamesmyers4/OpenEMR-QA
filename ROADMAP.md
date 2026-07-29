# ROADMAP.md

Forward-looking backlog, written 2026-07-28 after a full audit of `CONTEXT.md`, `TEST-PLAN.md`, `HANDOFF.md`, `FINDINGS.md`, `API-RESPONSE-SHAPES.md`, `README.md`, and `treeLine-output/FEEDBACK-FOR-TREELINE.md`. Everything originally scoped in `TEST-PLAN.md` is now `[x]` — this file is where new work goes next, not a rehash of what's already checked off there.

**Format note for whoever (human or Claude Code) picks this up:** each item below is scoped to be one Claude Code session — read the "Why" and "Do" sections, do the work, run it, flip the relevant `TEST-PLAN.md`/`FINDINGS.md`/`CONTEXT.md` checkbox or entry if applicable, then **stop and let the user review/commit manually** rather than chaining into the next item. Don't start the next session's work in the same sitting unless explicitly asked to. When a session is finished, update this file: change its status from `Open` to `Done`, and add one line noting the outcome (same terse style `HANDOFF.md` uses for its dated updates) — don't delete finished items, so this file keeps a running history the way `FINDINGS.md` does.

Sessions are grouped by theme, not strict priority, but roughly ordered high-value/low-effort first within each group. Pick whichever fits the time available.

---

## Group A — Small, well-defined bug fixes (good first sessions, low ambiguity)

### Session 1: Fix Firefox actionability timeout on patient-portal's "New Appointment" link
**Status:** Done (investigated, not resolved — see outcome below and Session 1b for the narrowed-down follow-up)

**Outcome (2026-07-29):** Reproduced the failure first, then found `{ force: true }` doesn't actually fix it — it just changes the failure signature (from a clean actionability timeout to a "detached from DOM, retrying" loop, still failing 10/10 in repeated trials). A throwaway diagnostic probe traced the real problem one level upstream: the top-nav `Calendar` label click itself already fails Firefox's actionability check in this test's specific flow, and even a forced click never actually flips the Calendar tab's container out of `display:none` — the tab genuinely never becomes visible, forced or not. Ruled out duplicate-element targeting, visual occlusion by the credentials modal (the existing manual teardown does work), and plain timing (a 10s settle wait didn't help). The `{ force: true }` change was reverted rather than shipped, since it doesn't work and would misrepresent the fix as done. Full trail in `CONTEXT.md`'s new decision-log entry and `HANDOFF.md` item 6. See Session 1b below for the specific, narrower theory worth testing next.

---

### Session 1b: Test whether the credentials-modal teardown workaround is what's blocking the Calendar tab-switch
**Status:** Open

**Why:** Session 1's investigation narrowed this down considerably — it's not a generic Firefox flakiness shrug, it's a specific, reproducible stuck state following one specific sequence (open the portal-credentials modal → the modal doesn't reliably self-close within a few seconds → the test manually strips its DOM nodes and the `body.modal-open` class instead of triggering its real close path → the top-nav's Calendar click stops working). The visual symptoms of "a modal is open" are confirmed fully cleaned up by that workaround (`elementFromPoint` no longer resolves to the modal afterward), yet the tab-switch still doesn't work — pointing at leftover *JS view-model* state, not DOM/CSS state.

**Do:**
1. Find the credentials modal's real close mechanism instead of guessing — read `create_portallogin.php` and whatever dialog framework opened it (likely the same `dlgopen()`/Bootstrap modal family already documented elsewhere in this repo) for its actual close callback or close button, and check whether that path fires any event (`hidden.bs.modal`, a custom callback, etc.) that the current DOM-stripping workaround skips entirely.
2. Test the leading theory directly: this app's top-nav is Knockout.js-bound (`data-bind="click: menuActionClick, css: {menuDisabled: ! enabled()}"`). From the browser console (or a `page.evaluate`), check whether there's an inspectable Knockout view-model / observable behind `enabled()` for the nav menu, and whether it's stuck `false` after this test's modal workaround — even though the `menuDisabled` CSS class itself wasn't observed applied, so this needs direct inspection, not just re-confirming the class.
3. If reachable, try invoking the modal's real close callback (matching this project's own established pattern of "invoke the popup's own JS callback directly instead of clicking through it," already used elsewhere for `dlgopen()` popups) instead of the current raw DOM removal, and see if that alone lets the subsequent Calendar tab click work normally, no `{ force: true }` needed anywhere.
4. If the modal doesn't reliably self-close within a reasonable wait even via its real API (not just DOM inspection), that's itself worth a note in `CONTEXT.md`'s existing `library/dialog.js` reliability entry, since it would extend that already-documented, still-unresolved finding rather than being a new one.

**Definition of done:** Either a real fix (verified via repeated Firefox trials, matching the ~9-11 trial rigor the rest of this investigation used) with `CalendarPage.openNewAppointmentForm()` needing no `{ force: true }` at all because the tab-switch works cleanly, or — if the theory doesn't pan out — an honest update to `CONTEXT.md`/`HANDOFF.md` closing out what was tested and ruled out, same as Session 1's own outcome. This is explicitly allowed to end without a fix; don't force one.

---

### Session 2: Root-cause the Bootstrap nested-dropdown timing race in DuplicatePatientsPage
**Status:** Open

**Why:** The same Firefox investigation left one thing genuinely unsolved, not just mitigated: `Admin` → `Patients` dropdown sometimes never visibly opens, root cause unknown, currently papered over with a retry-until-visible loop (`clickUntilNextIsVisible()`). This is real, uninvestigated app-timing behavior, not a test-authoring bug — worth a dedicated dig now that the two adjacent, better-understood bugs are fixed and won't confuse the signal.

**Do:**
1. Use a throwaway diagnostic probe script (same technique `HANDOFF.md` describes using for the `getByRole` a11y-tree gap) — dump the dropdown's DOM state / Bootstrap event firing at the moment a click fails to open it.
2. Try to isolate: is this a CSS-transition race, a JS event-listener-attachment-order issue, a `nested-dropdown` z-index/overlay issue, or something else?
3. If a real root cause is found, consider whether the fix belongs in the retry loop (replace polling with a deterministic wait on the right condition) or is purely an app-level bug worth a new `FINDINGS.md` entry (if it's a genuine OpenEMR UI defect, not a test artifact).

**Definition of done:** Either the retry-loop mitigation gets replaced with a real fix (and `CONTEXT.md`'s existing decision-log entry updated to say "resolved," not just "mitigated"), or the investigation comes up empty and that's written up honestly as still-open with what was ruled out — same standard as every other investigation in this repo. This is explicitly a "might not resolve cleanly" session; don't force a conclusion.

---

## Group B — Grey-area reliability phase: explicitly-flagged unfinished sub-areas

`CONTEXT.md`'s Purpose & Vision item 3 scoped this phase to cover race conditions, partial failures, *and* non-concurrency timing-dependent bugs. Only race conditions/concurrency has been touched so far (`FINDINGS.md` #18-22). The other two sub-areas are still fully open. `HANDOFF.md`'s Immediate Next Steps also names two specific untried concurrency candidates.

### Session 3: Facility concurrent-create race test
**Status:** Open

**Why:** `HANDOFF.md` explicitly flags this as an untried candidate: "worth checking whether it shares the Practitioner-style unguarded-uniqueness shape or the Patient-style guarded-but-mishandled shape before assuming either." Facility has both a `POST` create and (per `API-RESPONSE-SHAPES.md`) real validation — an unexplored data point for the existing race-condition pattern library.

**Do:**
1. Reuse the `Task.WhenAll`-firing pattern from `ConcurrencyApiTests.cs` (see `tests/OpenEmr.Api.Tests/GreyArea/ConcurrencyApiTests.cs` for the established style — raw concurrent `curl` first to confirm behavior live, per this project's "confirm, don't guess" standard, before writing the C# test).
2. Fire several concurrent `POST /api/facility` requests with identical or colliding identifying fields (check `FacilityValidator`/`FacilityService::insert()` source first for what, if anything, is supposed to be unique — name? code?).
3. Determine which shape it matches: DB-guarded-but-app-mishandled (like Patient's `pid`, `FINDINGS.md` #18) or fully unguarded (like Practitioner's `username`, `FINDINGS.md` #21) — or something new.

**Definition of done:** New test(s) in `ConcurrencyApiTests.cs` (or a new `FacilityConcurrencyApiTests.cs` if it grows large), a new `FINDINGS.md` entry if a real defect is confirmed (following the existing #18-22 format exactly), `TEST-PLAN.md`'s Layer 4 section updated with the new line, test actually run via `dotnet test` filtered to the new class, full suite re-run afterward per this project's standing "always run the full suite, not just the new test" rule (a prior session's Practitioner race broke an unrelated test via leaked fixture data — don't repeat that).

---

### Session 4: Document category-creation concurrency race
**Status:** Open

**Why:** The second explicitly-named untried candidate in `HANDOFF.md`. Document upload already has several confirmed defects around category-path resolution (`FINDINGS.md` #2, #9) — worth checking whether concurrent uploads to a *new* category path (one that doesn't exist yet, forcing simultaneous category-row creation) race in an interesting way, distinct from the already-covered single-record races.

**Do:**
1. Read `DocumentService::getLastIdOfPath()`/`createDocument()` (already partially read for #2/#9) specifically for what happens when the category doesn't exist yet — does it create one, and if so, is that creation itself guarded?
2. If category auto-creation isn't actually a thing (worth confirming before building a test around an assumption — check whether `path` must always pre-exist), pivot this session to concurrent uploads targeting the *same* existing category/patient instead, checking for any interesting interleaving in the `categories_to_documents` link-row writes.
3. Follow the same live-repro-before-test-code discipline as every other grey-area test in this project.

**Definition of done:** Same bar as Session 3 — new test(s), a `FINDINGS.md` entry only if something real is found (it's entirely plausible this comes back "confirmed safe," like the `lists_touch` follow-up did — that's a valid, valuable outcome too, not a failure), `TEST-PLAN.md` updated either way, full suite re-run.

---

### Session 5: Partial-failure scenario testing (new sub-phase)
**Status:** Open

**Why:** `CONTEXT.md`'s Purpose & Vision explicitly names "partial failures" as one of three grey-area sub-areas, alongside race conditions (now covered) and timing-dependent bugs (Session 6). This one hasn't been started at all — it's conceptually different from concurrency: what happens when a single request's own multi-step write is interrupted or partially fails, not what happens between two concurrent requests.

**Do:**
1. Identify real multi-table/multi-step write paths in this API to target — the most promising candidate already flagged in this project's own findings is `merge_patients.php`'s per-table delete/update loop (`FINDINGS.md` #22's root cause section: "every `deleteRows()`/`updateRows()`/`mergeRows()` call is an independent, unguarded pair with no lock between the count and the write" — no transaction wraps the whole operation). A network-level or process-level interruption mid-merge is a *different* angle on the same code than the concurrency test already covers.
2. Other candidates worth considering: Document upload (file write + DB insert + category link — three separate steps per `FINDINGS.md` #9's root cause), Encounter creation, or Patient Insurance's multi-field update.
3. Since this project can't literally kill the OpenEMR PHP process mid-request from a black-box test, the practical technique is likely: find an operation with an early step that can be made to fail via a crafted-but-plausible payload (e.g., a foreign-key-violating value, an overflow like `FINDINGS.md` #12's `billing.encounter` int overflow) partway through a known multi-step service method, and confirm via direct DB query whether earlier steps' writes were left in place (no transaction rollback) or not.
4. This is a research-heavy, exploratory session — it's fine if the first attempt is spent narrowing down which operation is actually a good candidate before writing any assertion.

**Definition of done:** At minimum, a documented decision (in `CONTEXT.md`'s Decision Log, matching the style of the existing "why X was scoped down" entries) of which operation was chosen and why, plus either a working test demonstrating a confirmed partial-failure defect (new `FINDINGS.md` entry) or a confirmed-safe result if the operation turns out to be properly transactional. Update `CONTEXT.md`'s Purpose & Vision line (currently: "partial failures and non-concurrency timing-dependent bugs remain open") once this sub-area has real coverage.

---

### Session 6: Non-concurrency timing-dependent bug hunting (new sub-phase)
**Status:** Open

**Why:** The third named-but-unstarted grey-area sub-area from `CONTEXT.md`. Distinct from both concurrency and partial failures — bugs that depend on *when* something happens, not *what else* is happening at the same time.

**Do:** Some concrete candidates worth investigating (pick one or two, don't try to boil the ocean in one session):
1. **Appointment date/time boundary behavior** — what happens booking an appointment exactly at midnight, across a DST transition (if this environment's timezone observes it), or with `pc_endTime` computed from a `pc_duration` that pushes past midnight into the next day?
2. **OAuth2 token timing near expiry** — `CONTEXT.md` already documents that testing true 1-hour expiry is impractical, but there may be a more tractable timing question nearby: does a token issued at time T behave consistently for a request that straddles a clock change, or is there any server-side clock-skew handling worth checking?
3. **Audit log timestamp ordering** — `log` table writes are PHP-mediated (`FINDINGS.md` #8); confirm whether rapid sequential writes to the same resource always produce audit rows in a consistent, queryable chronological order, or whether coarse timestamp granularity can produce same-timestamp rows with ambiguous ordering.
4. **The recurring-appointment expansion logic** (`CONTEXT.md`'s note: "the calendar expands the single stored `pc_recurrtype`/`pc_recurrspec` row dynamically per viewed date") — worth checking whether viewing a date far in the future, or right at a recurrence-series boundary (the last occurrence, `form_enddate`), renders correctly.

**Definition of done:** Same bar as Session 5 — this is exploratory, so the deliverable is either a confirmed new finding (with test coverage and a `FINDINGS.md` entry) or a documented "investigated, confirmed safe" result. Update `CONTEXT.md`'s Purpose & Vision line once there's real coverage here, matching the update made in Session 5.

---

## Group C — Coverage gaps already named as open in existing docs

### Session 7: Resolve or properly root-cause the Observation Vitals-category FHIR 500
**Status:** Open

**Why:** `CONTEXT.md`'s Known Constraints section documents this as a genuinely unresolved gap: reproducing the `form_vitals`+`forms` linkage via direct SQL produced an unexplained `500` that was only attempted once, with `encounter = 0` and no further variation tried, and the Apache error log check that session "logged nothing useful, possibly due to log rotation or output buffering." `ObservationApiTests.cs` currently only has the same bare bundle-shape check as Patient/Appointment — one tier below the patient-filtered/fixture-backed coverage every other new FHIR resource got.

**Do:**
1. First confirm the real Apache error log path on the running container (the prior session's own notes flag this as unconfirmed) — check the actual log location/rotation config before assuming the earlier "nothing useful" result was a dead end.
2. Retry the `form_vitals` + `forms` linkage insert with variations: a real `encounter` id from an actual `form_encounter` row (not `0`), and confirming `user`/`groupname` resolve to real `users` rows (per the existing note's own suggested next steps).
3. Once the `500` is understood (or avoided), bring `ObservationApiTests.cs` up to the same tier as `EncounterApiTests.cs`/`AllergyApiTests.cs`/`ConditionApiTests.cs`/`MedicationRequestApiTests.cs` — a patient-filtered search that asserts a specific fixture-created entry is actually returned, not just a valid bundle shape.

**Definition of done:** Either a working patient-scoped Observation/Vitals test (parity with the other 4 resources) plus a `CONTEXT.md` update replacing the "unexplained 500, not yet root-caused" language with the real explanation, or — if it turns out to be a genuine OpenEMR-side bug — a new `FINDINGS.md` entry documenting it properly instead of leaving it as a footnote.

---

### Session 8: Broaden the accessibility gate beyond 3 pages
**Status:** Open

**Why:** `accessibility.spec.ts` currently gates on `critical`-impact axe-core violations for exactly 3 pages (login, calendar, patient registration) — deliberately scoped narrow for the initial pass. The treeLine crawl baseline (`treeLine-output/openemr-qa/reports/axe-report.md`) covers 95 pages and 607 violations, almost entirely `serious`/`moderate`, but this project's own *live, current* gate is much narrower than the surface this project has actually built UI automation for (billing, clinical encounter, admin duplicate-merge, patient portal all have working POMs/specs now that never existed when the accessibility work was scoped).

**Do:**
1. Extend `accessibility.spec.ts` to scan at least: the billing payment/claim screens, the clinical encounter SOAP-note screen, the admin duplicate-merge screens, and the patient portal login/home pages — reusing the existing page objects (`BillingPaymentPage`, `BillingManagerPage`, `EncounterPage`, `DuplicatePatientsPage`, `PatientPortalPage`) to navigate there rather than raw URLs (this project's own established navigation-fragility lesson).
2. Keep the same `critical`-only gate philosophy for now — this session is about breadth of pages scanned, not about lowering the severity bar (that's a separate, bigger decision the user should make deliberately, not something to slip in silently).
3. Document any new `critical` violations found the same way `FINDINGS.md` #15/#16 did, with exact rule ids, not a vague "some accessibility issues."

**Definition of done:** More pages under the live accessibility gate, any new critical findings written up in `FINDINGS.md` following the #15/#16 format, `TEST-PLAN.md`'s Accessibility line updated to reflect the wider scope.

---

## Group D — New coverage breadth (the matrix is done; these are natural next layers)

### Session 9: Add a second k6 load scenario targeting a known concurrency defect
**Status:** Open

**Why:** `load/appointment-booking.js` is currently the only k6 scenario, and it tests a case (double-booking) that's a confirmed *non*-defect (soft warning, works fine under load). It would be a stronger portfolio/coverage story to also load-test one of the confirmed *broken* concurrency behaviors — e.g., does the Message lost-update rate (`FINDINGS.md` #19: "6-7 of 10 concurrent appends survive") get meaningfully worse at k6-scale concurrency (50-100+ VUs) than at the 10-request scale `ConcurrencyApiTests.cs` uses, or does it plateau?

**Do:**
1. Write a new k6 script (`load/message-update-race.js` or similar) modeled on `appointment-booking.js`'s structure — same OAuth2 bootstrap pattern via `load/run-load-test.mjs` (extend it to support running either/both scenarios).
2. Have it create one message, fire N concurrent `PUT`s with distinct markers at real load-test scale, and assert/report the survival rate via a custom k6 metric, the same way the existing script uses `teardown()` to read back results.
3. Compare the result to the existing 10-request C# test's ~60-70% survival rate — report whether it holds, improves, or degrades at scale.

**Definition of done:** New k6 scenario, runnable via `node load/run-load-test.mjs` (with a way to select which scenario, if you extend the orchestrator to support more than one), findings from the run documented as an addendum to `FINDINGS.md` #19 (not a new numbered entry, since it's the same underlying defect at a different scale) — matching how #20's write-up already frames itself as "concurrency-amplified" relative to #4.

---

### Session 10: Expand the RBAC/authorization-boundary test matrix at the API layer
**Status:** Open

**Why:** `CrossCuttingApiTests.cs`'s RBAC coverage is currently one confirmed pair: a token scoped only to `user/patient.read` can call `GET /api/patient` but gets `401` on `GET /api/facility`. That's a real, valuable proof that scope enforcement works — but it's one data point out of the ~13 REST resources and 7 FHIR resources this project covers. A broader matrix would more thoroughly validate (or find gaps in) `scope_check()`'s per-route enforcement.

**Do:**
1. Pick 3-4 more resource pairs spanning different ACL sections (per `CONTEXT.md`'s existing notes on which resources need `admin`/`super` vs `patients`/`demo` etc. — Facility and Patient Insurance are both already documented as needing broader ACL grants for writes) and confirm read/write scope enforcement holds for each.
2. Specifically worth checking: does a `write`-scoped-only token get correctly rejected from `read` operations (the inverse of the existing test), and does a token with a *FHIR* capitalized scope but not the matching lowercase REST scope (or vice versa) get correctly rejected from the other API surface — directly exercising the case-sensitivity distinction `CONTEXT.md`'s Decision Log already documents as a real, confirmed gotcha.
3. Reuse `OAuthTokenFixture`'s existing client-registration pattern, requesting a narrower scope set per new test case.

**Definition of done:** A handful of new `[Theory]`/`[Fact]` cases in `CrossCuttingApiTests.cs` (or a new dedicated `RbacApiTests.cs` if it grows past a few cases), each following this project's `.Because()` raw-body-capture convention, run and passing, `TEST-PLAN.md`'s RBAC line updated to reflect the broader matrix.

---

### Session 11: Document CSRF-token reusability and check login lockout/throttling behavior
**Status:** Open

**Why:** Two related, currently-undocumented security facts surfaced incidentally during other work but were never written up as their own findings: (1) `CONTEXT.md`'s Decision Log casually notes `CsrfUtils`'s tokens "are stateless HMACs derived from the session's private key, not single-use" — a real security-relevant fact (a leaked/logged CSRF token remains valid for the life of the session, not just one request) that was used as an *enabler* for the merge-race test but never itself asserted as a finding. (2) `CONTEXT.md` already confirms rate limiting/throttling is off globally, but that check was against the `globals` table generally — login-attempt lockout specifically (a distinct, common security control) was never directly tested.

**Do:**
1. Write a focused test/probe confirming CSRF token reusability: fetch a token from one page load, use it successfully on two different, temporally-separated requests in the same session, confirm both succeed. Decide whether this rises to a `FINDINGS.md` entry (Low/Medium severity, "not single-use CSRF tokens" is a real, if minor, defense-in-depth gap) — write it up if so, following the existing security-finding format (#15-16, or the confirmed-safe format used in `CONTEXT.md`'s security paragraph if the team decides this is working-as-intended rather than a defect).
2. Separately, test login lockout: fire N consecutive failed login attempts (via `LoginPage`) against the same account and confirm whether the account/IP gets locked out, throttled, or remains fully open indefinitely. This is a genuine security gap if confirmed absent — document either way (confirmed-safe or a new finding) the same way the SQL-injection/XSS/session-fixation checks in `security.spec.ts` already are.

**Definition of done:** New test(s) in `security.spec.ts` (or a new `CrossCuttingApiTests.cs` case for the CSRF check if that's more naturally an API-level probe), a documented result either way in `FINDINGS.md` or `CONTEXT.md`'s Known Constraints (matching the existing "three security/negative assumptions... all came back confirmed-safe" paragraph's style), `TEST-PLAN.md`'s Security/negative line updated.

---

### Session 12: Add dedicated test coverage for FINDINGS.md #12 (billing.encounter overflow)
**Status:** Open

**Why:** #12 is the one finding in the whole list explicitly marked as having "no dedicated test, since reproducing it would require an intentionally-overflowing insert with no useful assertion beyond 'MariaDB rejects it.'" That reasoning undersells what's actually testable here: the assertion isn't just "it fails," it's "this schema mismatch is real and this project's own Encounter fixtures are large enough to trigger it" — a genuinely useful regression guard against, e.g., a future schema migration silently changing `billing.encounter`'s column width.

**Do:**
1. Add a DB-layer test (likely in `FormEncounterDbTests.cs` or a new small test) that inserts a `form_encounter` row with a realistic large `encounter` value (matching this project's existing `DateTime.UtcNow.Ticks`-derived fixture pattern), then attempts a `billing` insert referencing it, and asserts the specific MariaDB "Out of range value" failure occurs — inside a transaction that's rolled back, per this project's established insert/verify/rollback pattern.
2. This documents the *current* broken state as an explicit, checked assertion rather than prose — if a future OpenEMR version or local schema tweak widens the column, this test starts failing in an informative way (schema assumption changed) rather than silent, undetected drift.

**Definition of done:** New test added, run once via `dotnet test` filtered to the class, `FINDINGS.md` #12's "Automated coverage" line updated from "no dedicated test" to reference the new test.

---

## Group E — Portfolio/CI polish (small, high visual payoff)

### Session 13: Add CI status badges and a lightweight flaky-test tracking note to README
**Status:** Open

**Why:** This project has real, working scheduled CI (daily smoke, weekly regression, auto-filing GitHub Issues on failure) that's currently invisible from the README — a portfolio reviewer skimming the repo has no immediate visual signal that this thing actually runs on a schedule and stays green, which is one of this project's stated differentiators (`CONTEXT.md`'s Purpose & Vision item 1: "behaves like a real team's CI... not a portfolio artifact that only gets exercised when someone happens to look at it"). Separately, this project has documented, real, known flaky spots (the patient-registration duplicate-check dialog, the two Firefox investigations) that are currently only mentioned in prose scattered across `HANDOFF.md`/`CONTEXT.md` — a single, findable "known flaky tests" note would make that easier to trust at a glance.

**Do:**
1. Add GitHub Actions status badges to the top of `README.md` for `ci.yml`, `scheduled-smoke.yml`, and `scheduled-regression.yml` (standard `https://github.com/{owner}/{repo}/actions/workflows/{file}/badge.svg` markdown image syntax) — this requires knowing the actual GitHub remote URL, so confirm it against `git remote -v` first rather than guessing the owner/repo slug.
2. Add a short "Known flaky spots" section to `README.md` or `HANDOFF.md` (pick whichever fits better — probably `HANDOFF.md`, since `README.md` is meant to stay relatively stable) consolidating: the patient-registration duplicate-check popup flakiness (absorbed by retries), and the still-open patient-portal Firefox issue (unless Session 1 already resolved it by the time this session runs — check first).

**Definition of done:** Badges rendering correctly (verify by viewing the rendered README on GitHub, not just the raw markdown), a consolidated flaky-spots note that links back to the relevant `CONTEXT.md` decision-log entries rather than duplicating their content.

---

## Not included here, and why

- **treeLine's own tool improvements** (`treeLine-output/FEEDBACK-FOR-TREELINE.md`'s 5 suggestions: network capture, navigation-aware crawling, cross-layer signal emission, identifier sanitization, combobox-name bounding) — these are feedback *for the treeLine tool's own codebase*, not actionable work inside this repo. Nothing to schedule here.
- **Fixing the 22 `FINDINGS.md` defects themselves** — deliberately out of scope per this project's own framing (`CONTEXT.md`: "OpenEMR is the system under test, not this repo's own code"). This project's job is to find and document real defects, not patch a third-party production system's source. Don't spend a session "fixing" `MessageService::update()`'s missing `pid` filter, for example — that's not what this repo is for.
- **Code coverage tooling** — explicitly ruled out in `CLAUDE.md`'s "Things not to do" section. Not revisiting that decision here.
