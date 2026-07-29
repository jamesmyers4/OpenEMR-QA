import type { Page, Locator, FrameLocator, Frame } from '@playwright/test'

export class CalendarPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.page.locator('.menuLabel', { hasText: 'Calendar' }).first().click({ force: true })
    await this.page.waitForTimeout(500)
  }

  content(): FrameLocator {
    return this.page.frameLocator('iframe[name="cal"]')
  }

  newAppointmentLink(time: string): Locator {
    return this.content().locator(`a[title="New Appointment"]:has-text("${time}")`)
  }

  existingAppointmentElement(identifyingText: string): Locator {
    return this.content().locator('div.event', { hasText: identifyingText }).first()
  }

  eventFrame(): Frame | null {
    return this.page.frame({ url: /add_edit_event\.php/ })
  }

  async openNewAppointmentForm(time: string): Promise<void> {
    await this.newAppointmentLink(time).click()
  }

  async openExistingAppointment(identifyingText: string): Promise<void> {
    await this.existingAppointmentElement(identifyingText).dblclick()
    const deadline = Date.now() + 10000
    while (Date.now() < deadline && !this.eventFrame()) {
      await this.page.waitForTimeout(200)
    }
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
    await this.page.waitForTimeout(1000)
  }

  async setDateAndTime(date: string, hour: string, minute: string): Promise<void> {
    const frame = this.eventFrame()
    await frame?.locator('#form_date').fill(date)
    await frame?.locator('#form_date').press('Escape')
    await frame?.locator('[name="form_hour"]').fill(hour)
    await frame?.locator('[name="form_minute"]').fill(minute)
  }

  async enableWeeklyRepeat(untilDate: string): Promise<void> {
    const frame = this.eventFrame()
    await frame?.locator('#form_repeat').check()
    await frame?.locator('[name="form_repeat_freq"]').selectOption('1')
    await frame?.locator('[name="form_repeat_type"]').selectOption('1')
    await frame?.locator('#form_enddate').fill(untilDate)
    await frame?.locator('#form_enddate').press('Escape')
  }

  async goToNextDay(): Promise<void> {
    await this.content().locator('#nextday').click()
    await this.page.waitForTimeout(500)
  }

  providerFilter(): Locator {
    return this.content().locator('#pc_username')
  }

  async filterToProviders(usernames: string[]): Promise<void> {
    await this.providerFilter().selectOption(usernames)
    await this.page.waitForTimeout(500)
  }
}
