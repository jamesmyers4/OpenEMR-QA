import { test, expect } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'

function pidFromDemographicsUrl(url: string): string {
  const match = url.match(/[?&]set_pid=(\d+)/)
  if (!match) {
    throw new Error(`could not extract pid from ${url}`)
  }
  return match[1]
}

test.describe('Admin duplicate-patient management - grey area concurrency', () => {
  test.describe.configure({ retries: 2 })

  test('two concurrent merge submissions for the same pair both report success despite no locking guard', async ({ page, context }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')

    const registration = new PatientRegistrationPage(page)

    async function createPatient(lastName: string): Promise<string> {
      await registration.goto()
      await registration.fillRequiredFields('Race', lastName, '1985-03-02', 'Female')
      await registration.submitCreate()
      await registration.confirmDuplicateCheck()
      await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
      const demoFrame = page.frames().find(frame => frame.url().includes('demographics.php'))
      return pidFromDemographicsUrl(demoFrame!.url())
    }

    const targetPid = await createPatient(`MergeRaceTarget${Date.now()}`)
    const sourcePid = await createPatient(`MergeRaceSource${Date.now()}`)

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
})
