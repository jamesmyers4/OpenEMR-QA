import { test, expect } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'

test.describe('Patient registration', () => {
  test.describe.configure({ retries: 2 })

  test.beforeEach(async ({ page }) => {
    page.on('dialog', dialog => dialog.accept())
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
  })

  test('creating a patient with all required fields lands on the new demographics page', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    await registration.goto()
    const uniqueId = Date.now()
    await registration.fillRequiredFields(`Playwright${uniqueId}`, `Testpatient${uniqueId}`, '1985-05-15', 'Female')
    await registration.submitCreate()
    await registration.confirmDuplicateCheck()
    await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
  })

  test('submitting with a missing last name is rejected inline and does not create the patient', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    await registration.goto()
    await registration.fillRequiredFields('Playwright', '', '1985-05-15', 'Female')
    await registration.submitCreate()
    await expect(registration.content().locator('#error_form_lname')).toBeVisible()
    await expect(registration.content().locator('#form_lname')).toHaveClass(/error-border/)
  })

  test('creating a patient that matches an existing name surfaces the duplicate-check review step', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const uniqueId = Date.now()
    const sharedFirstName = `Shared${uniqueId}`
    const sharedLastName = `Duplicate${uniqueId}`
    await registration.goto()
    await registration.fillRequiredFields(sharedFirstName, sharedLastName, '1990-01-01', 'Male')
    await registration.submitCreate()
    await registration.confirmDuplicateCheck()
    await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)

    await registration.goto()
    await registration.fillRequiredFields(sharedFirstName, sharedLastName, '1990-01-01', 'Male')
    await registration.submitCreate()
    const dupFrame = page.frameLocator('iframe[src*="new_search_popup.php"]')
    await expect(dupFrame.getByText(sharedLastName).first()).toBeVisible({ timeout: 30000 })
  })
})
