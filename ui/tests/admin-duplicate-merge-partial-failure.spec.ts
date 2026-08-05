import { test, expect } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { join } from 'node:path'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'

const composeFile = join(__dirname, '..', '..', 'docker', 'docker-compose.yml')

function pidFromDemographicsUrl(url: string): string {
  const match = url.match(/[?&]set_pid=(\d+)/)
  if (!match) {
    throw new Error(`could not extract pid from ${url}`)
  }
  return match[1]
}

function runSql(sql: string): string {
  return execFileSync('docker', [
    'compose', '-f', composeFile, 'exec', '-T', 'mariadb',
    'mariadb', '-uopenemr', '-popenemr', 'openemr', '-N', '-e', sql
  ], { encoding: 'utf8' })
}

async function createPatient(page: import('@playwright/test').Page, registration: PatientRegistrationPage, lastName: string): Promise<string> {
  await registration.goto()
  await registration.fillRequiredFields('Race', lastName, '1985-03-02', 'Female')
  await registration.submitCreate()
  await registration.confirmDuplicateCheck()
  await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
  const demoFrame = page.frames().find(frame => frame.url().includes('demographics.php'))
  return pidFromDemographicsUrl(demoFrame!.url())
}

test.describe('Admin duplicate-patient management - grey area partial failure', () => {
  test.describe.configure({ retries: 2 })

  test('a merge that hits a unique-constraint collision partway through the per-table loop dies mid-migration, leaving some tables migrated and others untouched', async ({ page, context }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')

    const registration = new PatientRegistrationPage(page)

    const targetPid = await createPatient(page, registration, `PartialFailTarget${Date.now()}`)
    const sourcePid = await createPatient(page, registration, `PartialFailSource${Date.now()}`)

    // merge_patients.php's main loop walks every table returned by SHOW TABLES (alphabetical on
    // this MariaDB version) and, for any table with a pid/patient_id column not otherwise special-
    // cased, runs a plain unconditional UPDATE ... SET pid = target WHERE pid = source - no
    // transaction wraps the whole operation (see FINDINGS.md #22's root cause). patient_access_onsite
    // has a single-column UNIQUE KEY on pid alone, so seeding one row for each patient here means the
    // re-point UPDATE for the source's row collides with the target's own pre-existing row and fails
    // outright. Confirmed live before writing this assertion: sqlStatement() routes that failure to
    // HelpfulDie(), which is a bare PHP exit - not a caught, rolled-back error.
    runSql(`INSERT INTO patient_access_onsite (pid, portal_username) VALUES (${targetPid}, 'target_onsite_${Date.now()}')`)
    runSql(`INSERT INTO patient_access_onsite (pid, portal_username) VALUES (${sourcePid}, 'source_onsite_${Date.now()}')`)

    // 'lists' sorts alphabetically before 'patient_access_onsite', so this row's UPDATE should
    // already have committed by the time the loop reaches and dies on patient_access_onsite.
    runSql(`INSERT INTO lists (date, type, title, begdate, pid, activity) VALUES (NOW(), 'medical_problem', 'Partial Failure Fixture Condition', CURDATE(), ${sourcePid}, 1)`)

    // 'pnotes' sorts alphabetically after 'patient_access_onsite', so this row should never be
    // reached at all once the loop dies - it should still show the source pid afterward.
    runSql(`INSERT INTO pnotes (date, body, pid, title) VALUES (NOW(), 'Partial failure fixture message', ${sourcePid}, 'Other')`)

    const mergeUrl = `/interface/patient_file/merge_patients.php?pid1=${targetPid}&pid2=${sourcePid}`
    const confirmResponse = await context.request.get(mergeUrl)
    const confirmHtml = await confirmResponse.text()
    const csrfMatch = confirmHtml.match(/name="csrf_token_form" value="([^"]+)"/)
    if (!csrfMatch) {
      throw new Error(`could not extract csrf_token_form from the merge confirmation page, response body was: ${confirmHtml}`)
    }

    const form = {
      csrf_token_form: csrfMatch[1],
      form_target_pid: targetPid,
      form_source_pid: sourcePid,
      form_submit: 'merge'
    }

    const mergeResponse = await context.request.post(mergeUrl, { form })
    const mergeBody = await mergeResponse.text()

    expect(mergeResponse.status(), `HelpfulDie() only ever calls PHP's bare exit - it never sets a non-200 status code, matching the same "raw SQL error page reported as a normal response" pattern already documented for FINDINGS.md #2/#9, response body was: ${mergeBody}`).toBe(200)
    expect(mergeBody, `merge_patients.php's own generic table loop should die on patient_access_onsite's UNIQUE KEY (pid) collision with HelpfulDie()'s raw "Query Error" page, not report a clean "Merge complete.", response body was: ${mergeBody}`).toContain('Query Error')
    expect(mergeBody, `response body was: ${mergeBody}`).not.toContain('Merge complete.')

    const listsPid = runSql(`SELECT pid FROM lists WHERE title = 'Partial Failure Fixture Condition' AND pid IN (${targetPid}, ${sourcePid})`).trim()
    expect(listsPid, `'lists' sorts before 'patient_access_onsite' in SHOW TABLES order, so its UPDATE should have already committed - each sqlStatement() call is its own auto-committed statement with no surrounding transaction - before the script died`).toBe(targetPid)

    const pnotesPid = runSql(`SELECT pid FROM pnotes WHERE body = 'Partial failure fixture message' AND pid IN (${targetPid}, ${sourcePid})`).trim()
    expect(pnotesPid, `'pnotes' sorts after 'patient_access_onsite', so the loop should have died before ever reaching it - the source patient's message should still be exactly where it started`).toBe(sourcePid)

    const onsiteRows = runSql(`SELECT pid FROM patient_access_onsite WHERE pid IN (${targetPid}, ${sourcePid}) ORDER BY pid`).trim().split('\n').filter(Boolean)
    expect(onsiteRows, `the failing UPDATE itself should not have partially applied - both the source's original row and the target's pre-existing row should remain exactly as seeded, still colliding, rows found: ${JSON.stringify(onsiteRows)}`).toEqual([targetPid, sourcePid].sort())

    const sourcePatientStillExists = runSql(`SELECT COUNT(*) FROM patient_data WHERE pid = ${sourcePid}`).trim()
    expect(sourcePatientStillExists, `'patient_data' sorts after 'patient_access_onsite' too, so the source patient's own deleteRows() call - the step that would normally make the source patient disappear on a successful merge - never ran either. The net effect: the source patient still fully exists as its own record, while one of its clinical rows (the Condition above) has already been silently reassigned to the target patient - a real, confirmable partially-cannibalized patient record from a single failed request, not a race between two requests.`).toBe('1')
  })
})
