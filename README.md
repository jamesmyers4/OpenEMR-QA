# OpenEMR Test Suite

Full-stack test automation portfolio project: API and DB tests in C#/xUnit, UI tests in Playwright/TypeScript, run against a self-hosted OpenEMR instance.

See `docs/CONTEXT.md` for the framing/stack decisions and `docs/TEST-PLAN.md` for the coverage matrix and current progress.

## Prerequisites

- Docker + Docker Compose
- .NET 10 SDK
- Node 22+

## 1. Start the target system

```
cd docker
docker compose up -d
```

Wait for the OpenEMR container to report healthy (`docker compose ps`) before running any tests — first boot runs migrations and demo-data seeding and can take a few minutes.

Default seeded admin credentials: `admin` / `pass`.2

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

Run `npx playwright test --ui` for the interactive runner while you're building out new specs, or `npx playwright codegen https://localhost:9300` to confirm real selectors against the running container before writing more UI specs — the ones in `pages/` are a best-effort starting point, not verified against a live instance yet.

## Repo layout

```
CLAUDE.md                 instructions for Claude Code sessions in this repo
docker/                  docker-compose stack for OpenEMR + MariaDB
tests/
  OpenEmr.Api.Tests/      REST + FHIR API tests
  OpenEmr.Db.Tests/       direct MariaDB verification tests
ui/
  pages/                  Playwright page objects
  tests/                  Playwright specs
docs/
  CONTEXT.md              stack decisions, open questions
  TEST-PLAN.md            full coverage matrix and build order
.github/workflows/ci.yml  spins up the stack and runs all three layers
```

## Status

Scaffolding is in place with a working example in each layer (Patient + Appointment on the API side, matching DB checks, auth + scheduling on the UI side). The bulk of resource/table/workflow coverage in `docs/TEST-PLAN.md` is still `[ ]` — that's the actual work.
