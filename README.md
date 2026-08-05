# OpenEMR Test Suite

Full-stack test automation portfolio project: API and DB tests in C#/xUnit, UI tests in Playwright/TypeScript, load tests in k6, all run against a self-hosted OpenEMR instance.

See `CONTEXT.md` for the framing/stack decisions, `TEST-PLAN.md` for the coverage matrix, `FINDINGS.md` for the standalone write-ups of confirmed defects, `API-RESPONSE-SHAPES.md` for a per-resource response-shape quick reference, and `ROADMAP.md` for what's planned next now that the original coverage matrix is fully scaffolded.

## Prerequisites

- Docker + Docker Compose
- .NET 10 SDK
- Node 22+

## 1. Start the target system

```
cd docker
docker compose up -d
docker compose ps
```

Wait for both `mariadb` and `openemr` to report `healthy` before running any tests — first boot runs schema install + SSL cert generation and can take several minutes; `openemr` can show `unhealthy` for a while first, which is normal, not a failure. No manual first-login browser steps are required — `LoginPage.loginAs()` auto-dismisses the product-registration modal, and `docker-compose.yml`'s `OPENEMR_SETTING_*` env vars already enable the REST/FHIR/OAuth-password-grant/portal connectors.

Default seeded admin credentials: `admin` / `pass`.

Run `node docker/reset-env.mjs` any time you want a full `down -v && up -d` reset with health-polling built in — useful for clearing accumulated local fixture data (CI already resets fresh every run).

## 2. Run the API + DB tests

```
cd tests
dotnet restore OpenEmr.Tests.sln
dotnet test OpenEmr.Tests.sln
```

## 3. Run the UI tests

```
cd ui
npm install
npx playwright install
npx playwright test
```

Run `npx playwright test --ui` for the interactive runner while building out new specs, or `npx playwright codegen https://localhost:9300` to confirm real selectors against the running container before writing more UI specs.

## 4. Run the load test

```
node load/run-load-test.mjs
```

Bootstraps a dedicated OAuth2 client against the running stack, then fires `load/appointment-booking.js` (k6): 20 concurrent virtual users booking the identical appointment slot, asserting no lost writes/id collisions under real contention.

## Test coverage at a glance

| Layer | Framework | Test files | Test cases |
| --- | --- | --- | --- |
| API (Legacy REST + FHIR R4 + cross-cutting) | C#/xUnit | 18 | 94 |
| Database | C#/xUnit | 8 | 33 |
| UI (Playwright, run on chromium + firefox in full regression) | TypeScript | 11 spec files | 27 |
| Load | k6 | 1 scenario | threshold-based, not a pass/fail count |

**154 automated test cases** across the three core layers (127 C# + 27 UI), plus schema-validated FHIR contract checks, an axe-core accessibility gate, and a k6 load scenario. Coverage is tracked as scenario coverage against the matrix in `TEST-PLAN.md` (100% of that matrix is currently scaffolded), not line/branch coverage — see `TEST-PLAN.md`'s note on why that's the right metric for a third-party system under test.

24 confirmed, source-root-caused defects are documented in `FINDINGS.md` (severity Critical→Low), found across the "straightforward coverage" build-out and a deliberate later "grey area" concurrency/reliability phase (`TEST-PLAN.md`'s Layer 4). Most are real OpenEMR-side bugs left intentionally undocumented-as-fixed, since the point of this project is to detect and document defects in the system under test, not patch a third-party codebase.

## Repo layout

```
CLAUDE.md                     instructions for Claude Code sessions in this repo
CONTEXT.md                    permanent reference: purpose, architecture, stack decisions, known constraints/decision log
TEST-PLAN.md                  full coverage matrix (Layers 1-4) and build order
FINDINGS.md                   standalone write-ups of the 24 most serious confirmed defects
API-RESPONSE-SHAPES.md        per-resource quick reference: create status, list envelope, not-found shape
ROADMAP.md                    forward-looking backlog, broken into individually workable sessions
docker/
  docker-compose.yml          OpenEMR + MariaDB stack
  reset-env.mjs                full down -v / up -d reset with health-polling, for local hygiene
tests/
  OpenEmr.Api.Tests/           REST + FHIR API tests, plus GreyArea/ (concurrency) and CrossCutting/
  OpenEmr.Db.Tests/            direct MariaDB verification tests, plus GreyArea/ and AuditLog/
ui/
  pages/                       Playwright page objects
  tests/                       Playwright specs
load/
  appointment-booking.js       k6 load scenario
  run-load-test.mjs            OAuth2 bootstrap orchestrator for the k6 run
treeLine-output/               output + feedback from a treeLine authenticated-crawl tool run
.github/workflows/
  ci.yml                       push/PR/manual dispatch, full suite
  run-suite.yml                shared reusable workflow (docker up, dotnet test, playwright test, teardown)
  scheduled-smoke.yml          daily cron: API+DB plus auth.spec.ts on chromium
  scheduled-regression.yml     weekly cron: full suite, chromium+firefox, matches push/PR coverage
```

## Status

All three test layers (API, DB, UI) are feature-complete against the original `TEST-PLAN.md` coverage matrix, including the full FHIR resource expansion, cross-cutting API concerns (OAuth2, pagination, RBAC, malformed input), accessibility, security/negative testing, contract/schema validation, load testing, and a "grey area" concurrency/reliability phase that has already surfaced 5 additional confirmed race-condition defects. CI runs on every push/PR plus a daily smoke cron and a weekly full-regression cron, both auto-filing/closing a GitHub Issue on failure/recovery. See `ROADMAP.md` for what's next now that the original scope is done.
