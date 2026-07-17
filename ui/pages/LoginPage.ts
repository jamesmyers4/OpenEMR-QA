import type { Page, Locator } from '@playwright/test'

export class LoginPage {
  readonly page: Page
  readonly usernameInput: Locator
  readonly passwordInput: Locator
  readonly submitButton: Locator

  constructor(page: Page) {
    this.page = page
    this.usernameInput = page.locator('input[name="authUser"]')
    this.passwordInput = page.locator('input[name="clearPass"]')
    this.submitButton = page.locator('#login_button')
  }

  async goto(): Promise<void> {
    await this.page.goto('/interface/login/login.php?site=default')
  }

  async loginAs(username: string, password: string): Promise<void> {
    await this.usernameInput.fill(username)
    await this.passwordInput.fill(password)
    await this.submitButton.click()
  }
}
