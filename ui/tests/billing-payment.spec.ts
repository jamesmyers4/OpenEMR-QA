import { test, expect, type Page } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'
import { EncounterPage } from '../pages/EncounterPage'
import { BillingPaymentPage } from '../pages/BillingPaymentPage'

async function createTestPatientWithVisit(page: Page, registration: PatientRegistrationPage, encounter: EncounterPage): Promise<string> {
  const uniqueId = Date.now()
  const lastName = `BillTest${uniqueId}`
  await registration.goto()
  await registration.fillRequiredFields('Bill', lastName, '1979-06-12', 'Male')
  await registration.submitCreate()
  await registration.confirmDuplicateCheck()
  await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
  await encounter.goto()
  await expect.poll(() => !!encounter.visitFormFrame(), { timeout: 10000 }).toBe(true)
  await encounter.createVisit('Billing coverage test visit')
  await expect.poll(() => page.frames().some(frame => frame.url().includes('encounter_top.php')), { timeout: 10000 }).toBe(true)
  return lastName
}

test.describe('Billing payment', () => {
  test.describe.configure({ retries: 2 })

  test.beforeEach(async ({ page }) => {
    page.on('dialog', dialog => dialog.accept())
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
  })

  test('applying a cash payment against a patient visit shows a payment receipt', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const encounter = new EncounterPage(page)
    await createTestPatientWithVisit(page, registration, encounter)

    const billing = new BillingPaymentPage(page)
    await billing.goto()
    await expect.poll(() => !!billing.content(), { timeout: 10000 }).toBe(true)
    await billing.applyPayment('cash', '25.00')

    await expect.poll(() => billing.content()?.locator('body').innerText() ?? '', { timeout: 10000 }).toContain('Receipt for Payment')
    await expect(billing.content()!.locator('td.bg-color-w', { hasText: '25.00' })).toBeVisible()
  })
})
