import type { Page, Locator, FrameLocator, Frame } from '@playwright/test'

export class CalendarPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.page.locator('.menuLabel', { hasText: 'Calendar' }).first().click()
    await this.page.waitForTimeout(500)
  }

  content(): FrameLocator {
    return this.page.frameLocator('iframe[name="cal"]')
  }

  newAppointmentLink(time: string): Locator {
    return this.content().locator(`a[title="New Appointment"]:has-text("${time}")`)
  }

  existingAppointmentLink(time: string): Locator {
    return this.content().locator(`a.event_time:has-text("${time}")`).first()
  }

  eventFrame(): Frame | null {
    return this.page.frame({ url: /add_edit_event\.php/ })
  }

  async openNewAppointmentForm(time: string): Promise<void> {
    await this.newAppointmentLink(time).click()
  }

  async openExistingAppointment(time: string): Promise<void> {
    await this.existingAppointmentLink(time).click()
  }

  async fillAppointment(title: string, patientPid: number, patientLastName: string, patientFirstName: string, patientDob: string): Promise<void> {
    const frame = this.eventFrame()
    await frame?.locator('#form_title').fill(title)
    await frame?.evaluate(({ pid, lname, fname, dob }) => {
      type WindowWithSetPatient = Window & { setpatient?: (pid: number, lname: string, fname: string, dob: string) => void }
      ;(window as WindowWithSetPatient).setpatient?.(pid, lname, fname, dob)
    }, { pid: patientPid, lname: patientLastName, fname: patientFirstName, dob: patientDob })
    await this.page.waitForTimeout(300)
  }

  async save(): Promise<void> {
    const frame = this.eventFrame()
    const button = await frame?.$('#form_save')
    await button?.click()
    await this.page.waitForTimeout(300)
    await this.page.evaluate(() => {
      document.querySelectorAll('.dialogModal').forEach(el => el.remove())
      document.querySelectorAll('.modal-backdrop').forEach(el => el.remove())
    })
  }

  async deleteCurrentEvent(): Promise<void> {
    const frame = this.eventFrame()
    const button = await frame?.$('#form_delete')
    await button?.click()
  }
}
