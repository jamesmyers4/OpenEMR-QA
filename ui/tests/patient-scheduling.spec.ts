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

    await calendar.openExistingAppointment(patient.lastName)
    await calendar.deleteCurrentEvent()
    await expect(calendar.content().getByText(patient.lastName)).toHaveCount(0)
  })

  test('rescheduling an appointment updates its displayed time', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const patient = await createTestPatient(page, registration)
    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm('13:00')
    await calendar.fillAppointment('Follow-up', patient.pid, patient.lastName, patient.firstName, patient.dob)
    await calendar.save()
    const eventElement = calendar.existingAppointmentElement(patient.lastName)
    await expect(eventElement).toContainText('13:00')

    await calendar.openExistingAppointment(patient.lastName)
    await calendar.setDateAndTime('2026-07-25', '14', '00')
    await calendar.save()

    await expect(eventElement).toContainText('14:00')
    await expect(eventElement).not.toContainText('13:00')
  })

  test('a weekly recurring appointment appears on the following week', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const patient = await createTestPatient(page, registration)
    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm('15:00')
    await calendar.fillAppointment('Follow-up', patient.pid, patient.lastName, patient.firstName, patient.dob)
    await calendar.enableWeeklyRepeat('2026-08-15')
    await calendar.save()
    await expect(calendar.content().getByText(patient.lastName)).toBeVisible()

    for (let i = 0; i < 7; i++) {
      await calendar.goToNextDay()
    }
    await expect(calendar.content().getByText(patient.lastName)).toBeVisible()
  })

  test('clearing the provider filter falls back to the current user instead of showing nothing', async ({ page }) => {
    const registration = new PatientRegistrationPage(page)
    const patient = await createTestPatient(page, registration)
    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm('16:00')
    await calendar.fillAppointment('Follow-up', patient.pid, patient.lastName, patient.firstName, patient.dob)
    await calendar.save()
    await expect(calendar.content().getByText(patient.lastName)).toBeVisible()

    await calendar.filterToProviders([])
    await expect(calendar.content().getByText(patient.lastName)).toBeVisible()
  })
})
