# CLAUDE.md

Instructions for Claude Code when working in this repo. Read `CONTEXT.md` first for the why; read `TEST-PLAN.md` for what's currently built vs. still open — that file is the source of truth for what to work on next.

## Before making changes

1. Check `TEST-PLAN.md` for the item being worked on and its current `[ ]`/`[x]` status.
2. If the item touches OpenEMR API fields, table names, or UI selectors you're not certain about, verify against the running container (`docker compose -f docker/docker-compose.yml up -d`, then hit the endpoint or inspect the DOM/table directly) or against `API_README.md` in the OpenEMR repo rather than guessing. Several selectors and a couple of schema assumptions in this repo are best-effort placeholders — do not assume existing code is already verified just because it's present.

## Running things

```
cd docker && docker compose up -d
cd tests && dotnet restore OpenEmr.Tests.sln && dotnet test OpenEmr.Tests.sln
cd ui && npm install && npx playwright install && npx playwright test
```

`npx playwright codegen https://localhost:9300` against the running container is the fastest way to confirm real selectors before writing a new UI spec.

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
- Corresponding checkbox in `docs/TEST-PLAN.md` flipped from `[ ]` to `[x]`
- If it revealed an OpenEMR schema/API detail that contradicts something written in `CONTEXT.md` (table name, field name, response shape), update `CONTEXT.md` too — that file should stay accurate, not just aspirational

## Things not to do

- Don't add cross-project dependencies between `OpenEmr.Api.Tests` and `OpenEmr.Db.Tests` — see the decoupling rationale in `docs/CONTEXT.md`.
- Don't add code coverage tooling (coverlet, etc.) as a proxy for "done" — OpenEMR is the system under test, not this repo's own code. Coverage is tracked as scenario coverage in `docs/TEST-PLAN.md`, not line coverage.
- Don't silently change the pinned OpenEMR image version or `OPENEMR_SETTING_*` env vars in `docker/docker-compose.yml` without noting it in `docs/CONTEXT.md`'s known-constraints section — that surface has changed between OpenEMR releases before.
