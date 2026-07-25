# API-RESPONSE-SHAPES.md

Quick reference for "what does this endpoint actually return" — every resource in this project's Legacy REST layer turned out to have its own answer to basic questions like create status code, list envelope shape, and not-found behavior. This table pulls together what's already been confirmed and logged in `CONTEXT.md`'s Known Constraints; it doesn't re-verify anything, just organizes it so the pattern (and the lack of one) is visible at a glance. See `CONTEXT.md` for the full source-level evidence behind any row.

## Legacy REST (`/apis/{site}/api`)

| Resource | Create status | List envelope | Single-record not-found | Gotcha |
| --- | --- | --- | --- | --- |
| Patient | `201 Created`, `data`-wrapped | `data` array | not explicitly tested in this suite | Single `GET`/`PUT` key on the patient `uuid`, not `pid` — `pid` only works for list/create/nested routes |
| Appointment | `200 OK` (not `201`), bare `{"id": N}` | bare array root; an empty list returns `404`, not `200` with `[]` | not explicitly tested for a nonexistent `eid` | `GET /api/appointment/{eid}` always returns an array, even for one record; there is no `PUT`/`PATCH` route at all |
| Encounter | `201 Created`, `data`-wrapped (`data.euuid`) | `data`-wrapped array | not explicitly tested | Route param is the patient `puuid`, not `pid`; `PUT` (close/sign) requires an explicit `user`/`group` the `POST` path injects automatically |
| Practitioner | `201 Created`, `data`-wrapped | `data`-wrapped array | `404` | Creating without a `username` leaves the record permanently invisible to both list and get, despite a successful `201` |
| Facility | `201 Created`, `data`-wrapped | `data`-wrapped array | `400` (not `404`) | A malformed/nonexistent uuid fails validation before the controller's own not-found branch runs, so it's `400`, unlike Practitioner's `404` |
| Insurance Company | `500` — unconditionally broken (`validate()` doesn't exist on the service) | bare `{}` when empty, not `{"data": []}` like every other resource | `404` | `POST`/`PUT` always `500`; `GET` (list/get-by-id) is unaffected |
| Patient Insurance | `200 OK` (not `201`), `data`-wrapped | `data`-wrapped array | `404` | `PUT` is a full, unconditional column overwrite — any field left out of the payload is silently nulled, not preserved |
| Allergy | `201 Created`, `data`-wrapped | `data`-wrapped array via the top-level `?puuid=` filter; the patient-nested list route always returns `{"data": []}` regardless of real data | `404` | The patient-nested `GET` routes are broken (uuid compared as a string against a numeric column) — always use the top-level route with a `puuid` filter instead |
| Immunization | n/a — no `POST` route exists at all (`404`, not `405`) | `data`-wrapped array | `404` | Read/search-only; unlike Procedure/Prescription, the list route *does* honor a `patient_id` filter |
| Procedure | n/a — no `POST` route (`404`) | `data`-wrapped array, but any query-string filter (e.g. `patient_id`) is silently ignored | `500` (not `404`) whenever a row's `encounter_id`/`provider_id` doesn't resolve | Read/search-only; both list and get-by-id crash with a bare `500` on any row with an unresolved reference — not a contrived edge case |
| Prescription | n/a — no `POST` route (`404`) | `data`-wrapped array, filter also silently ignored | `500` unconditionally, valid uuid or not | Single-record `GET` is completely broken — the generated query references a column (`_id`) that doesn't exist |
| Document | `200 OK` with the bare literal JSON value `true` — not an object, no envelope at all | bare array root; a real, valid category with zero documents returns `404`, not `200` with `[]` | `400` (download by nonexistent id) | Omitting the required `path` query parameter doesn't validate cleanly — it returns `200` with a raw HTML SQL-error page as the body, leaking server file paths |
| Message | `201 Created`, bare `{"mid": N}` (numeric) | n/a — no `GET`/list route exists at all (`404`) | n/a | `PUT` returns `{"mid": "N"}` (the same value, but as a string); `PUT`/`DELETE` both report `200` success even when zero rows matched; `PUT` never filters by `pid` — a real cross-patient bug |

## FHIR R4 (`/apis/{site}/fhir`)

| Resource | Shape |
| --- | --- |
| Patient, Appointment, Encounter, AllergyIntolerance, Condition, MedicationRequest, Observation | All identical: `{"resourceType": "Bundle", "type": "collection", "total": N, "link": [...], "entry": [...]}` |

**Gotcha that applies to all seven:** a bare, unfiltered `GET /fhir/{Resource}` can return a technically-valid, non-empty `Bundle` made up of unrelated leftover fixture data from other test runs — this proves the endpoint parses and authenticates, but nothing about patient-scoped search actually working. Confirmed live (see `CONTEXT.md`) that the same search filtered to one specific patient's `puuid` can return `total: 0` even when the bare search looked fine. A FHIR `Patient.id` is exactly the same UUID as the REST `puuid` already used everywhere in this project, so no id-translation is needed when building a `?patient=` filter.

## What "no pattern" actually means here

Every column above varies at least once across the 13 resources — there is no safe default to assume for a 14th resource added later:

- **Create status**: `201` is the plurality, but Appointment and Patient Insurance both use `200`, and Insurance Company's create doesn't work at all.
- **List envelope**: `data`-wrapped is the plurality, but Appointment and Document both return a bare array at the root, and empty-list handling itself splits three ways (`200` with `[]` doesn't happen anywhere — it's either a real empty array inside `data`, a bare `[]`, or a `404`).
- **Not-found**: `404` is the plurality, but Facility uses `400` for a bad uuid.

Always verify per-resource against the live instance before assuming a sibling resource's shape carries over — this table exists so that verification doesn't have to start from zero.
