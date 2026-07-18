# TREELINE-BUILDOUT-SESSION.md

One-time task brief. Read this in full before touching code. Read `docs/CONTEXT.md`, `docs/HANDOFF.md`, `docs/TEST-PLAN.md`, and `CLAUDE.md` first if you haven't already this session — this file assumes that context and doesn't repeat it.

## What just happened

A `treeLine` run (separate AI-powered Playwright site-comprehension tool) was pointed at this project's OpenEMR instance. Its output lives at `treeLine-output/openemr-qa/`. This is new — the UI layer has been sitting scaffolded-but-unverified (per `HANDOFF.md`), and this output is the first real shot at closing that gap systematically instead of guessing selectors one page at a time.

**Trust level on the output:** treeLine should be fairly reliable on POM structure and selectors, but it is not guaranteed correct. If treeLine's output and the live running UI disagree on anything, the live UI wins, full stop. A disagreement means treeLine has a logic bug worth noting, not that the UI is wrong.

## Order of operations — do not reorder

1. **Start Docker first.** `cd docker && docker compose up -d`. First boot after a volume reset takes several minutes (schema install + Apache startup) — kick this off before you start reading anything, so it's ready by the time you need it instead of becoming a blocking wait later.
2. **While that's coming up, read the treeLine output in full.** `treeLine-output/openemr-qa/` — list the directory structure first to understand what got captured, then read every file in it. Build a mental (or written, scratch is fine) map of: which pages/screens were crawled, what selectors were captured per element, what workflows/forms were traced, and whether anything beyond pure UI (network calls, API responses, DB-adjacent data) is present anywhere in the output.
3. **Sanity-check treeLine against the one known-good data point.** `ui/pages/LoginPage.ts` already has a confirmed-correct selector: `#login-button`. Find how treeLine captured that same element. If it matches, that's a decent calibration signal for trusting the rest of the output. If it doesn't match, flag that immediately — it changes how much weight the rest of the output should get.
4. **Build out Page Objects under `ui/pages/` from the treeLine output**, one per screen/workflow it identified, following the existing POM pattern and this repo's code style exactly: no inline comments, one blank line between methods, no blank lines inside a method body.
5. **Wire up specs under `ui/tests/`** using those page objects — `test.describe` grouped by feature, plain-English `test('...')` names, per the existing convention.
6. **For anything treeLine didn't cover, or where its selectors look shaky, verify manually against the live container** — `npx playwright codegen https://localhost:9300` is the fastest path, same as `CLAUDE.md` already prescribes. Don't invent selectors from either treeLine or memory; every selector in a new spec should trace back to either treeLine (cross-checked) or a live codegen/DOM inspection.
7. **Actually run the specs** — `npx playwright test` — before handing anything back. Same rule the API/DB layers already follow: don't hand back a test that's only been read, not executed. Root-cause real failures the way the API layer's 4 known failures were root-caused, don't paper over them.
8. **Update the durable docs:**
   - `docs/TEST-PLAN.md` — flip checkboxes for whatever's now actually covered and passing
   - `docs/HANDOFF.md` — replace the "UI Layer Status" section, it's currently stale (says only login is verified)
   - `docs/CONTEXT.md` — only if something you find contradicts an existing assumption written there (selector, workflow shape, etc.)

## Separate deliverable: feedback on the treeLine output itself

This is explicitly requested — don't skip it even if the UI buildout runs long. Write it to a new file: `treeLine-output/FEEDBACK-FOR-TREELINE.md`. Cover:

**Usefulness for C#/API/DB tests, honestly assessed.** treeLine is a Playwright/UI tool, so the prior is that this output is UI-only and won't move the needle on the 4 known API/DB failures (FHIR scopes, appointment list per-item shape, double-booking conflict behavior, etc.) or on new API/DB coverage. Confirm or correct that against what's actually in the output — if there's any network-request capture (XHR/fetch calls seen during the crawl, response shapes, endpoint list) buried in there, that would directly matter for the API layer and should be called out specifically, not missed.

**Organization/reorganization feedback.** Was the output easy to map directly into POM files, or did it take significant translation? What's missing that would have made step 4 faster — e.g. per-selector element role/type, associated form field names, expected page states, stable-vs-fragile ID indicators? What's present but not actually useful, if anything?

**Concrete suggestions for a future treeLine capability aimed at API/DB test generation** — since Jimmy raised this directly: could treeLine capture the network requests fired during its crawl (method, endpoint, request/response shape) and emit that as a separate artifact per page? That alone would give the C# API layer a real evidence-based starting point instead of guessing at response shapes the way the appointment-list and double-booking failures currently require. Note this as a forward-looking recommendation, not something to build now.

## Guardrails

- This pass is UI-focused. Don't touch `OpenEmr.Api.Tests` or `OpenEmr.Db.Tests` unless the treeLine output genuinely contains API/DB-relevant evidence per the feedback step above — if it does, note it in the feedback file and stop there; don't scope-creep into fixing the known C# failures in the same session.
- No fabricated selectors. Every selector traces to treeLine (cross-checked) or live verification.
- Keep the three test layers decoupled — no new cross-project dependencies.
- Code style is non-negotiable: no comments, one blank line between members, none inside a function body.

## Definition of done

- Every page/workflow treeLine identified has a corresponding, verified Page Object in `ui/pages/`
- Specs exist and were actually executed against the live instance — passing specs are checked in as passing, failing ones are documented with root cause the same way the API layer's failures are documented in `HANDOFF.md`
- `TEST-PLAN.md` and `HANDOFF.md` reflect real, current state
- `treeLine-output/FEEDBACK-FOR-TREELINE.md` exists and gives an honest, specific answer on API/DB usefulness plus concrete improvement ideas
