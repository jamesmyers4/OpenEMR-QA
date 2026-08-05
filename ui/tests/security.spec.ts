import { test, expect } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { join } from 'node:path'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'

const composeFile = join(__dirname, '..', '..', 'docker', 'docker-compose.yml')

function runSql(sql: string): string {
  return execFileSync('docker', [
    'compose', '-f', composeFile, 'exec', '-T', 'mariadb',
    'mariadb', '-uopenemr', '-popenemr', 'openemr', '-N', '-e', sql
  ], { encoding: 'utf8' })
}

function bcryptHash(plaintext: string): string {
  return execFileSync('docker', [
    'compose', '-f', composeFile, 'exec', '-T', 'openemr',
    'php', '-r', `echo password_hash('${plaintext}', PASSWORD_BCRYPT);`
  ], { encoding: 'utf8' }).trim()
}

async function createPatient(page: import('@playwright/test').Page, registration: PatientRegistrationPage, lastName: string): Promise<string> {
  await registration.goto()
  await registration.fillRequiredFields('Race', lastName, '1985-03-02', 'Female')
  await registration.submitCreate()
  await registration.confirmDuplicateCheck()
  await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
  const demoFrame = page.frames().find(frame => frame.url().includes('demographics.php'))
  const match = demoFrame!.url().match(/[?&]set_pid=(\d+)/)
  if (!match) {
    throw new Error(`could not extract pid from ${demoFrame!.url()}`)
  }
  return match[1]
}

test.describe('Security', () => {
  test.describe.configure({ retries: 2 })

  test('an XSS-shaped patient last name is escaped, not executed, on the demographics page', async ({ page }) => {
    page.on('dialog', dialog => dialog.accept())
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
    const registration = new PatientRegistrationPage(page)
    await registration.goto()
    const uniqueId = Date.now()
    const xssMarker = `XSS${uniqueId}`
    const xssPayload = `<img src=x onerror="window.__xssFired=true">${xssMarker}`
    await registration.fillRequiredFields('Playwright', xssPayload, '1985-05-15', 'Female')
    await registration.submitCreate()
    await registration.confirmDuplicateCheck()
    await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
    const demographicsFrame = page.frames().find(frame => /summary\/demographics(_save)?\.php/.test(frame.url()))
    await expect.poll(() => demographicsFrame?.evaluate(() => document.body.innerText)).toContain(xssMarker)
    const xssFired = await page.evaluate(() => (window as unknown as { __xssFired?: boolean }).__xssFired)
    expect(xssFired).toBeUndefined()
    const bodyHtml = await demographicsFrame?.evaluate(() => document.body.innerHTML)
    expect(bodyHtml).not.toContain('<img src=x onerror=')
  })

  test('the session cookie is regenerated on login, not fixed from the pre-login session', async ({ page, context }) => {
    const login = new LoginPage(page)
    await login.goto()
    const beforeLoginCookie = (await context.cookies()).find(c => c.name === 'OpenEMR')?.value
    expect(beforeLoginCookie).toBeTruthy()
    await login.loginAs('admin', 'pass')
    const afterLoginCookie = (await context.cookies()).find(c => c.name === 'OpenEMR')?.value
    expect(afterLoginCookie).toBeTruthy()
    expect(afterLoginCookie).not.toBe(beforeLoginCookie)
  })

  test('a CSRF token scraped once remains valid across two temporally-separated requests against different resources', async ({ page, context }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')

    const registration = new PatientRegistrationPage(page)
    const firstTargetPid = await createPatient(page, registration, `CsrfReuseTargetA${Date.now()}`)
    const firstSourcePid = await createPatient(page, registration, `CsrfReuseSourceA${Date.now()}`)

    const firstMergeUrl = `/interface/patient_file/merge_patients.php?pid1=${firstTargetPid}&pid2=${firstSourcePid}`
    const confirmResponse = await context.request.get(firstMergeUrl)
    const confirmHtml = await confirmResponse.text()
    const csrfMatch = confirmHtml.match(/name="csrf_token_form" value="([^"]+)"/)
    if (!csrfMatch) {
      throw new Error(`could not extract csrf_token_form from the merge confirmation page, response body was: ${confirmHtml}`)
    }
    const scrapedToken = csrfMatch[1]

    const firstResponse = await context.request.post(firstMergeUrl, {
      form: { csrf_token_form: scrapedToken, form_target_pid: firstTargetPid, form_source_pid: firstSourcePid, form_submit: 'merge' }
    })
    const firstBody = await firstResponse.text()
    expect(firstBody, `the token's first use should succeed normally, response body was: ${firstBody}`).toContain('Merge complete.')

    // Wait a real, meaningful interval and perform an unrelated navigation in between, so the token's
    // second use below is genuinely a separate request some time later in the session, not just two
    // requests fired back-to-back that happen to be sequential.
    await page.waitForTimeout(5000)
    await registration.goto()

    const secondTargetPid = await createPatient(page, registration, `CsrfReuseTargetB${Date.now()}`)
    const secondSourcePid = await createPatient(page, registration, `CsrfReuseSourceB${Date.now()}`)

    const secondMergeUrl = `/interface/patient_file/merge_patients.php?pid1=${secondTargetPid}&pid2=${secondSourcePid}`
    const secondResponse = await context.request.post(secondMergeUrl, {
      form: { csrf_token_form: scrapedToken, form_target_pid: secondTargetPid, form_source_pid: secondSourcePid, form_submit: 'merge' }
    })
    const secondBody = await secondResponse.text()
    expect(
      secondBody,
      `CsrfUtils's tokens are stateless HMACs derived from the session's own private key, not single-use or bound to the specific page/resource they were issued for - the same token scraped once from the first merge confirmation page should still succeed here, several seconds later, against an entirely different pair of patients, response body was: ${secondBody}`
    ).toContain('Merge complete.')
  })

  test('a per-user login lockout threshold is enforced after repeated failed attempts, not left fully open', async ({ page }) => {
    test.slow()
    const username = `lockouttest${Date.now()}`
    const correctPassword = 'CorrectHorseBattery9!'
    const passwordHash = bcryptHash(correctPassword)
    runSql(
      `INSERT INTO users (username, fname, lname, active, authorized, calendar) VALUES ('${username}', 'Lockout', 'Test', 1, 1, 0)`
    )
    runSql(
      `INSERT INTO users_secure (id, username, password, login_fail_counter) SELECT id, '${username}', '${passwordHash}', 0 FROM users WHERE username = '${username}'`
    )
    // A users row alone cannot log in - AuthUtils::authenticateUser() also requires the username to
    // resolve to a real `groups` row (UserService::getAuthGroupForUser()) and a real phpGACL ARO group
    // membership (AclExtended::aclGetGroupTitles()), confirmed by reading source after an early version
    // of this test showed .login-failure firing but users_secure.login_fail_counter never incrementing -
    // that combination is the exact signature of failing one of these two earlier, ACL-related gates
    // instead of ever reaching the password/counter check at all. Mirrors admin's own real rows.
    runSql(`INSERT INTO groups (name, user) VALUES ('Default', '${username}')`)
    const aroId = Number(runSql('SELECT MAX(id) + 1 FROM gacl_aro').trim())
    runSql(
      `INSERT INTO gacl_aro (id, section_value, value, order_value, name, hidden) VALUES (${aroId}, 'users', '${username}', 10, '${username}', 0)`
    )
    runSql(`INSERT INTO gacl_groups_aro_map (group_id, aro_id) VALUES (11, ${aroId})`)

    try {
      const maxFailedLogins = Number(runSql(
        "SELECT gl_value FROM globals WHERE gl_name = 'password_max_failed_logins'"
      ).trim())
      expect(maxFailedLogins, 'this test asserts real lockout behavior against a live threshold - if this global is 0, the feature is off entirely and this test would be asserting nothing').toBeGreaterThan(0)

      const login = new LoginPage(page)
      for (let attempt = 0; attempt < maxFailedLogins; attempt++) {
        await login.goto()
        await login.submitCredentials(username, 'DefinitelyWrongPassword')
        await expect(page.locator('.login-failure')).toBeVisible()
      }

      const failCounter = Number(runSql(
        `SELECT login_fail_counter FROM users_secure WHERE username = '${username}'`
      ).trim())
      expect(failCounter, 'the failed-login counter should have tracked every deliberate failure above').toBeGreaterThanOrEqual(maxFailedLogins)

      await login.goto()
      await login.submitCredentials(username, correctPassword)
      await expect(
        page.locator('.login-failure'),
        `password_max_failed_logins=${maxFailedLogins} is enabled in this environment's globals, so a login with the CORRECT password should now still be rejected until the auto-reset window passes - AuthUtils::checkLoginFailedCounter() returns the same generic failure for this case as for a wrong password, with no distinct "locked out" message, confirmed by reading source before writing this assertion`
      ).toBeVisible()
    } finally {
      runSql(`DELETE FROM gacl_groups_aro_map WHERE aro_id = ${aroId}`)
      runSql(`DELETE FROM gacl_aro WHERE id = ${aroId}`)
      runSql(`DELETE FROM groups WHERE user = '${username}'`)
      runSql(`DELETE FROM users_secure WHERE username = '${username}'`)
      runSql(`DELETE FROM users WHERE username = '${username}'`)
    }
  })
})
