# CLAUDE.md

Instructions for Claude Code when working in this repo. Read `CONTEXT.md` first for the why; read `TEST-PLAN.md` for the coverage matrix (fully scaffolded as of 2026-07-28 — it's a record of what's built, not an open task list anymore). `ROADMAP.md` is the current source of truth for what to work on next, broken into individually workable sessions — each is meant to end with you stopping and letting the user commit manually rather than chaining into the next one. `HANDOFF.md` has the environment-setup narrative and build history, `FINDINGS.md` has standalone write-ups of the most serious confirmed defects, and `API-RESPONSE-SHAPES.md` is a per-resource quick reference for response shapes.

## Before making changes

1. Check `ROADMAP.md` for the session being worked on and its current `Open`/`Done` status. If you're picking up a `TEST-PLAN.md` item instead (rare now that the matrix is fully checked off), check its `[ ]`/`[x]` status there.
2. If the item touches OpenEMR API fields, table names, or UI selectors you're not certain about, verify against the running container (`docker compose -f docker/docker-compose.yml up -d`, then hit the endpoint or inspect the DOM/table directly) or against `API_README.md` in the OpenEMR repo rather than guessing. Several selectors and a couple of schema assumptions in this repo are best-effort placeholders — do not assume existing code is already verified just because it's present.

## Running things

```
cd docker && docker compose up -d
cd tests && dotnet restore OpenEmr.Tests.sln && dotnet test OpenEmr.Tests.sln
cd ui && npm install && npx playwright install && npx playwright test
node load/run-load-test.mjs
```

`npx playwright codegen https://localhost:9300` against the running container is the fastest way to confirm real selectors before writing a new UI spec.

`node docker/reset-env.mjs` does a full `docker compose down -v && up -d` reset and polls both containers to genuinely healthy — use it to clear accumulated local fixture data (CI already resets fresh every run, so this is purely a local-dev hygiene tool; see `TEST-PLAN.md`'s test-data-lifecycle entry).

On a genuinely fresh volume, wait for both `mariadb` and `openemr` to report `healthy` in `docker compose ps` before running anything — first boot can take several minutes and `openemr` can show `unhealthy` for a while first, which is normal, not a failure. No manual first-login browser steps are actually required before the test suites pass: `LoginPage.loginAs()` already auto-dismisses the product-registration modal, and `docker-compose.yml`'s `OPENEMR_SETTING_*` env vars already enable the REST/FHIR/OAuth-password-grant/portal connectors — confirmed by both CI (which never does any manual step) and a local `docker/reset-env.mjs` reset followed by a clean full `dotnet test` run.

## Code style — follow exactly, do not default to your usual style

- No inline comments, in C# or TypeScript. This is a deliberate choice for reading/writing practice, not something to "fix."
- One blank line after a function/method body ends, before the next member. No blank lines between statements inside a function body.
- C# test method naming: `MethodUnderTest_Scenario_ExpectedResult`.
- TS: `test.describe` grouping by feature, plain-English `test('...')` names.

## Patterns to reuse, not reinvent

- New API test class → `[Collection("OpenEmr API")]`, constructor takes `OAuthTokenFixture`, build paths with `OpenEmrEndpoints.Rest(...)` / `OpenEmrEndpoints.Fhir(...)`, do not hardcode `/apis/...` paths inline.
- New DB test class → `[Collection("OpenEmr DB")]`, constructor takes `DbConnectionFixture`. Prefer the insert/verify/rollback transaction pattern (see `Direct_Insert_Is_Immediately_Readable_Then_Rolled_Back` in `PatientDbTests.cs`) over tests that depend on the API project having run first — the two C# projects must stay independently runnable.
- New UI spec → add a page object under `ui/pages/` if one doesn't exist for that screen yet, don't put raw locators directly in spec files.

## Definition of done for a new test

- Follows the naming and fixture conventions above
- Actually run once (`dotnet test` filtered to the class, or `playwright test <file>`) — don't hand back a test that's only been read, not executed
- Corresponding checkbox in `TEST-PLAN.md` flipped from `[ ]` to `[x]`
- If it revealed an OpenEMR schema/API detail that contradicts something written in `CONTEXT.md` (table name, field name, response shape), update `CONTEXT.md` too — that file should stay accurate, not just aspirational

## Things not to do

- Don't add cross-project dependencies between `OpenEmr.Api.Tests` and `OpenEmr.Db.Tests` — see the decoupling rationale in `CONTEXT.md`.
- Don't add code coverage tooling (coverlet, etc.) as a proxy for "done" — OpenEMR is the system under test, not this repo's own code. Coverage is tracked as scenario coverage in `TEST-PLAN.md`, not line coverage.
- Don't silently change the pinned OpenEMR image version or `OPENEMR_SETTING_*` env vars in `docker/docker-compose.yml` without noting it in `CONTEXT.md`'s known-constraints section — that surface has changed between OpenEMR releases before.
