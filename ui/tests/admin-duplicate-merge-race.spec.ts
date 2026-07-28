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

test.describe('Admin duplicate-patient management - grey area concurrency', () => {
  test.describe.configure({ retries: 2 })

  test('two concurrent merge submissions for the same pair both report success despite no locking guard', async ({ page, context }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')

    const registration = new PatientRegistrationPage(page)

    const targetPid = await createPatient(page, registration, `MergeRaceTarget${Date.now()}`)
    const sourcePid = await createPatient(page, registration, `MergeRaceSource${Date.now()}`)

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

    const [firstResponse, secondResponse] = await Promise.all([
      context.request.post(mergeUrl, { form }),
      context.request.post(mergeUrl, { form })
    ])
    const firstBody = await firstResponse.text()
    const secondBody = await secondResponse.text()

    expect(firstResponse.status(), `first response body was: ${firstBody}`).toBe(200)
    expect(secondResponse.status(), `second response body was: ${secondBody}`).toBe(200)
    expect(firstBody, `merge_patients.php's per-table delete/update loop has no locking around it, so a genuinely concurrent second submission for the same pair is never rejected - both requests race through independently, first response body was: ${firstBody}`).toContain('Merge complete.')
    expect(secondBody, `same as above for the second concurrent request, second response body was: ${secondBody}`).toContain('Merge complete.')

    const thirdResponse = await context.request.post(mergeUrl, { form })
    const thirdBody = await thirdResponse.text()
    expect(thirdBody, `a subsequent, sequential merge attempt for the same pair should now find the source patient genuinely gone - proving the concurrent pair above did fully delete it exactly once with no crash, despite neither of them detecting the other's simultaneous submission, response body was: ${thirdBody}`).toContain('Source patient not found')
  })

  test('concurrent merges racing on a shared lists_touch type converge on one correct row, not data loss', async ({ page, context }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')

    const registration = new PatientRegistrationPage(page)

    const targetPid = await createPatient(page, registration, `ListsTouchRaceTarget${Date.now()}`)
    const sourcePid = await createPatient(page, registration, `ListsTouchRaceSource${Date.now()}`)

    // mergeRows()'s per-row date-comparison branch (used only for the lists_touch table) is a
    // distinct code path from the simple full-row deletes covered by the test above - a plausible,
    // previously-unprobed avenue for data loss under concurrency per CONTEXT.md's decision log.
    // The source row is seeded with the older date, which is the branch that deletes the target's
    // row and re-points the source's row at the target pid - the riskier of the two branches, since
    // a second racing request reading stale data before that write could plausibly re-delete the
    // just-migrated row and leave nothing behind.
    const touchType = 'medical_problem'
    runSql(`INSERT INTO lists_touch (pid, type, date) VALUES (${targetPid}, '${touchType}', '2024-06-15 00:00:00')`)
    runSql(`INSERT INTO lists_touch (pid, type, date) VALUES (${sourcePid}, '${touchType}', '2020-01-01 00:00:00')`)

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

    const [firstResponse, secondResponse] = await Promise.all([
      context.request.post(mergeUrl, { form }),
      context.request.post(mergeUrl, { form })
    ])
    const firstBody = await firstResponse.text()
    const secondBody = await secondResponse.text()

    expect(firstResponse.status(), `first response body was: ${firstBody}`).toBe(200)
    expect(secondResponse.status(), `second response body was: ${secondBody}`).toBe(200)
    expect(firstBody, `first response body was: ${firstBody}`).toContain('Merge complete.')
    expect(secondBody, `second response body was: ${secondBody}`).toContain('Merge complete.')

    const survivingRows = runSql(
      `SELECT pid, date FROM lists_touch WHERE type = '${touchType}' AND pid IN (${targetPid}, ${sourcePid})`
    ).trim().split('\n').filter(Boolean)

    expect(
      survivingRows,
      `exactly one lists_touch row for type='${touchType}' should survive the concurrent merge, carrying the target pid and the older (source's) date - confirmed live across repeated trials that this converges correctly rather than both rows being lost, unlike the simple-table merge race above which is a genuine defect. Rows found: ${JSON.stringify(survivingRows)}`
    ).toEqual([`${targetPid}\t2020-01-01 00:00:00`])
  })
})
