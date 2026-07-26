import type { Page, Locator } from '@playwright/test'

export class PatientPortalPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.page.goto('/portal/index.php?site=default')
  }

  usernameInput(): Locator {
    return this.page.locator('#uname').last()
  }

  passwordInput(): Locator {
    return this.page.locator('#pass').last()
  }

  async loginAs(username: string, password: string): Promise<void> {
    await this.usernameInput().fill(username)
    await this.passwordInput().fill(password)
    await this.page.locator('form').filter({ has: this.page.locator('#pass') }).last()
      .locator('button[type="submit"], input[type="submit"]').click()
  }

  async openAppointments(): Promise<void> {
    await this.page.getByRole('button', { name: 'Appointments', exact: true }).click()
  }

  futureAppointmentCards(): Locator {
    return this.page.locator('#appointmentcard .row').first().locator('.card')
  }
}
