import { test, expect, type Page } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { CalendarPage } from '../pages/CalendarPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'

interface TestPatient {
  pid: number
  firstName: string
  lastName: string
  dob: string
}

async function createTestPatient(page: Page, registration: PatientRegistrationPage): Promise<TestPatient> {
  const uniqueId = Date.now()
  const firstName = 'Sched'
  const lastName = `SchedTest${uniqueId}`
  const dob = '1988-03-03'
  await registration.goto()
  await registration.fillRequiredFields(firstName, lastName, dob, 'Male')
  await registration.submitCreate()
  await registration.confirmDuplicateCheck()
  await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
  const demoFrame = page.frames().find(frame => frame.url().includes('demographics.php'))
  const pid = Number(new URL(demoFrame!.url()).searchParams.get('set_pid'))
  return { pid, firstName, lastName, dob }
}

test.describe('Patient scheduling', () => {
  test.describe.configure({ retries: 3 })

  let lastDialogMessage = ''

  test.beforeEach(async ({ page }) => {
    lastDialogMessage = ''
    page.on('dialog', async dialog => {
      lastDialogMessage = dialog.message()
      await dialog.accept()
    })
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
  })

  test('booking a new appointment shows it on the calendar', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const patient = await createTestPatient(page, registration)
    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm('9:00')
    await calendar.fillAppointment('Follow-up', patient.pid, patient.lastName, patient.firstName, patient.dob)
    await calendar.save()
    await expect(calendar.content().getByText(patient.lastName)).toBeVisible()
  })

  test('double booking the same provider slot warns about provider availability', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const first = await createTestPatient(page, registration)
    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm('10:00')
    await calendar.fillAppointment('Intake', first.pid, first.lastName, first.firstName, first.dob)
    await calendar.save()

    const second = await createTestPatient(page, registration)
    await calendar.goto()
    await calendar.openNewAppointmentForm('10:00')
    await calendar.fillAppointment('Intake', second.pid, second.lastName, second.firstName, second.dob)

    await calendar.save()
    await expect.poll(() => lastDialogMessage).toContain('Provider not available')
  })

  test('canceling an appointment removes it from the day view', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const patient = await createTestPatient(page, registration)
    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm('11:00')
    await calendar.fillAppointment('Follow-up', patient.pid, patient.lastName, patient.firstName, patient.dob)
    await calendar.save()
    await expect(calendar.content().getByText(patient.lastName)).toBeVisible()

    await calendar.openExistingAppointment('11:00')
    await calendar.deleteCurrentEvent()
    await expect(calendar.content().getByText(patient.lastName)).toHaveCount(0)
  })
})
