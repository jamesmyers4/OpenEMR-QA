import { test, expect, type Page } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'
import { CalendarPage } from '../pages/CalendarPage'
import { DemographicsPage } from '../pages/DemographicsPage'
import { PatientPortalPage } from '../pages/PatientPortalPage'

interface TestPatient {
  pid: number
  firstName: string
  lastName: string
  dob: string
}

async function createTestPatient(page: Page, registration: PatientRegistrationPage, firstName: string, lastName: string, dob: string): Promise<TestPatient> {
  await registration.goto()
  await registration.fillRequiredFields(firstName, lastName, dob, 'Female')
  await registration.submitCreate()
  await registration.confirmDuplicateCheck()
  await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
  const demoFrame = page.frames().find(frame => frame.url().includes('demographics.php'))
  const pid = Number(new URL(demoFrame!.url()).searchParams.get('set_pid'))
  return { pid, firstName, lastName, dob }
}

test.describe('Patient portal', () => {
  test.describe.configure({ retries: 2 })

  test.beforeEach(async ({ page }) => {
    page.on('dialog', dialog => dialog.accept())
  })

  test('a patient can log into the portal with generated credentials and see only their own upcoming appointment', async ({ page, context }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')

    const uniqueId = Date.now()
    const registration = new PatientRegistrationPage(page)
    const patient = await createTestPatient(page, registration, 'Portia', `PortalTest${uniqueId}`, '1990-01-01')

    const demographics = new DemographicsPage(page)
    await demographics.openEditForm()
    await expect.poll(() => !!demographics.editFrame(), { timeout: 10000 }).toBe(true)
    await page.waitForTimeout(1500)
    await demographics.setContactEmail(`portaltest${uniqueId}@example.test`)
    await demographics.enablePatientPortal()
    await demographics.save()
    await expect.poll(() => demographics.dashboardFrame()?.url().includes('demographics_save.php'), { timeout: 15000 }).toBe(true)
    await page.waitForTimeout(1500)

    await expect.poll(async () => {
      await demographics.expandPortalCard()
      await page.waitForTimeout(500)
      return demographics.portalCardExpandedClass()
    }, { timeout: 15000 }).toContain('show')

    await demographics.openCreateCredentials()
    await expect.poll(() => !!demographics.credentialsFrame(), { timeout: 10000 }).toBe(true)
    const credentials = await demographics.readGeneratedCredentials()
    expect(credentials.username, 'a login username must actually be generated').not.toBe('')
    expect(credentials.password, 'a login password must actually be generated').not.toBe('')
    await demographics.saveCredentials()
    await page.waitForTimeout(1000)
    await page.evaluate(() => {
      document.querySelectorAll('.dialogModal').forEach(el => el.remove())
      document.querySelectorAll('.modal-backdrop').forEach(el => el.remove())
    })

    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm('9:00')
    await calendar.fillAppointment('Portal Test Visit', patient.pid, patient.lastName, patient.firstName, patient.dob)
    await calendar.save()
    await expect(calendar.content().getByText(patient.lastName)).toBeVisible()

    const portalPage = await context.newPage()
    const portal = new PatientPortalPage(portalPage)
    await portal.goto()
    await portal.loginAs(credentials.username, credentials.password)
    await expect(portalPage).toHaveURL(/portal\/home\.php/, { timeout: 15000 })

    await portal.openAppointments()
    await expect.poll(() => portal.futureAppointmentCards().count(), { timeout: 10000 }).toBe(1)
  })
})
