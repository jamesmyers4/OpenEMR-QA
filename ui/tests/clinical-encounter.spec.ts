import { test, expect, type Page } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'
import { EncounterPage } from '../pages/EncounterPage'

async function createTestPatientWithVisit(page: Page, registration: PatientRegistrationPage, encounter: EncounterPage): Promise<string> {
  const uniqueId = Date.now()
  const lastName = `EncTest${uniqueId}`
  await registration.goto()
  await registration.fillRequiredFields('Enc', lastName, '1982-02-02', 'Male')
  await registration.submitCreate()
  await registration.confirmDuplicateCheck()
  await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
  await encounter.goto()
  await expect.poll(() => !!encounter.visitFormFrame(), { timeout: 10000 }).toBe(true)
  await encounter.createVisit('Clinical encounter UI test visit')
  await expect.poll(() => page.frames().some(frame => frame.url().includes('encounter_top.php')), { timeout: 10000 }).toBe(true)
  return lastName
}

test.describe('Clinical encounter', () => {
  test.describe.configure({ retries: 2 })

  test.beforeEach(async ({ page }) => {
    page.on('dialog', dialog => dialog.accept())
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
  })

  test('adding a SOAP note and signing it locks the note', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const encounter = new EncounterPage(page)
    await createTestPatientWithVisit(page, registration, encounter)

    await expect.poll(() => !!encounter.formsFrame(), { timeout: 10000 }).toBe(true)
    await encounter.openSoapForm()
    await expect.poll(() => !!encounter.soapFormFrame(), { timeout: 10000 }).toBe(true)
    await encounter.fillAndSubmitSoapNote(
      'Patient reports mild headache.',
      'Vitals stable.',
      'Tension headache.',
      'OTC analgesics, follow up in 1 week.'
    )

    const formsFrame = encounter.formsFrame()!
    await expect(formsFrame.locator('body')).toContainText('Subjective: Patient reports mild headache.')
    await expect(formsFrame.locator('body')).toContainText('Plan: OTC analgesics, follow up in 1 week.')
    await expect(formsFrame.getByText('No signatures on file')).toHaveCount(2)

    await encounter.eSignLatestForm('pass')

    await expect(formsFrame.locator('body')).toContainText('Locked')
    await expect(formsFrame.getByText('No signatures on file')).toHaveCount(1)
  })
})
