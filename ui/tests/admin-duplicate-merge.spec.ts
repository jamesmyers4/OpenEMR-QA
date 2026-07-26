import { test, expect } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'
import { DuplicatePatientsPage } from '../pages/DuplicatePatientsPage'

function pidFromDemographicsUrl(url: string): string {
  const match = url.match(/[?&]set_pid=(\d+)/)
  if (!match) {
    throw new Error(`could not extract pid from ${url}`)
  }
  return match[1]
}

test.describe('Admin duplicate-patient management', () => {
  test.describe.configure({ retries: 2 })

  test.beforeEach(async ({ page }) => {
    page.on('dialog', dialog => dialog.accept())
  })

  test('two patients with matching name/DOB/sex are flagged as duplicates and can be merged', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')

    const lastName = `DupMerge${Date.now()}`
    const registration = new PatientRegistrationPage(page)

    async function createDuplicate(): Promise<string> {
      await registration.goto()
      await registration.fillRequiredFields('Dana', lastName, '1985-03-02', 'Female')
      await registration.submitCreate()
      await registration.confirmDuplicateCheck()
      await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
      const demoFrame = page.frames().find(frame => frame.url().includes('demographics.php'))
      return pidFromDemographicsUrl(demoFrame!.url())
    }

    const pid1 = await createDuplicate()
    const pid2 = await createDuplicate()
    expect(pid1, 'the two fixture patients must be distinct records, not the same one reused').not.toBe(pid2)

    const duplicatesPage = new DuplicatePatientsPage(page)
    await duplicatesPage.goto()
    await expect.poll(() => !!duplicatesPage.managerFrame(), { timeout: 60000 }).toBe(true)

    await expect.poll(() => duplicatesPage.mergeCandidateRow(lastName).count(), { timeout: 15000 })
      .toBe(1)

    await duplicatesPage.mergeAndDiscard(lastName)
    await expect.poll(() => !!duplicatesPage.mergeFrame(), { timeout: 15000 }).toBe(true)

    const mergeFrame = duplicatesPage.mergeFrame()!
    await expect(mergeFrame.getByRole('button', { name: 'Merge', exact: true })).toBeVisible()

    await duplicatesPage.confirmMerge()
    await expect(mergeFrame.getByText('Merge complete.')).toBeVisible({ timeout: 15000 })

    await duplicatesPage.returnToDuplicateManager()
    await expect.poll(() => duplicatesPage.managerFrame()?.url().includes('manage_dup_patients.php'), { timeout: 30000 }).toBe(true)

    await expect.poll(() => duplicatesPage.mergeCandidateRow(lastName).count(), { timeout: 15000 })
      .toBe(0)
  })
})
