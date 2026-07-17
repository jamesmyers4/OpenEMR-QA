import type { Page, Locator, FrameLocator } from '@playwright/test'

export class CalendarPage {
  readonly page: Page
  readonly newAppointmentLink: Locator

  constructor(page: Page) {
    this.page = page
    this.newAppointmentLink = page.locator('a', { hasText: 'New Appointment' })
  }

  async goto(): Promise<void> {
    await this.page.goto('/interface/main/main_screen.php?auth=login&site=default')
    await this.page.locator('a[href*="main/calendar/index.php"]').click()
  }

  content(): FrameLocator {
    return this.page.frameLocator('iframe[name="RTop"]')
  }

  async openNewAppointmentForm(): Promise<void> {
    await this.newAppointmentLink.click()
  }

  async fillAppointment(patientName: string, title: string, startTime: string): Promise<void> {
    const frame = this.content()
    await frame.locator('#form_patient').fill(patientName)
    await frame.locator('#form_title').selectOption({ label: title })
    await frame.locator('#form_start_time').fill(startTime)
  }

  async saveAppointment(): Promise<void> {
    await this.content().locator('#save_button').click()
  }
}
