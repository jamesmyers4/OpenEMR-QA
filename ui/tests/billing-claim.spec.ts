import { test, expect, type Page } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'
import { EncounterPage } from '../pages/EncounterPage'
import { BillingManagerPage } from '../pages/BillingManagerPage'

async function createTestPatientWithVisit(page: Page, registration: PatientRegistrationPage, encounter: EncounterPage): Promise<string> {
  const uniqueId = Date.now()
  const lastName = `ClaimTest${uniqueId}`
  await registration.goto()
  await registration.fillRequiredFields('Claim', lastName, '1975-11-11', 'Male')
  await registration.submitCreate()
  await registration.confirmDuplicateCheck()
  await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
  await encounter.goto()
  await expect.poll(() => !!encounter.visitFormFrame(), { timeout: 10000 }).toBe(true)
  await encounter.createVisit('Billing claim UI test visit')
  await expect.poll(() => page.frames().some(frame => frame.url().includes('encounter_top.php')), { timeout: 10000 }).toBe(true)
  return lastName
}

test.describe('Billing claim generation', () => {
  test.describe.configure({ retries: 2 })

  test.beforeEach(async ({ page }) => {
    page.on('dialog', dialog => dialog.accept())
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
  })

  test('generating a CMS 1500 claim for a billed encounter downloads a PDF', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const encounter = new EncounterPage(page)
    const lastName = await createTestPatientWithVisit(page, registration, encounter)

    await expect.poll(() => !!encounter.formsFrame(), { timeout: 10000 }).toBe(true)
    await encounter.openFeeSheet()
    await expect.poll(() => !!encounter.feeSheetFrame(), { timeout: 10000 }).toBe(true)
    await encounter.addEstablishedPatientDetailedVisitCode()
    await expect(encounter.feeSheetFrame()!.locator('body')).toContainText('99213')
    await encounter.saveFeeSheet()

    const billingManager = new BillingManagerPage(page)
    await billingManager.goto()
    await expect.poll(() => !!billingManager.content(), { timeout: 10000 }).toBe(true)
    await expect.poll(() => billingManager.rowFor(lastName)?.count() ?? 0, { timeout: 10000 }).toBeGreaterThan(0)

    await billingManager.selectForBilling(lastName)
    const download = await billingManager.generateCms1500Pdf()

    expect(download.suggestedFilename()).toContain('.pdf')
  })
})
