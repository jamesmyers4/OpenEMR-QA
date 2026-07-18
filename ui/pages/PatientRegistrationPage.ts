import type { Page, Locator, FrameLocator } from '@playwright/test'

export class PatientRegistrationPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.page.locator('.menuLabel', { hasText: 'Patient' }).first().click()
    await this.page.getByText('New/Search', { exact: true }).click()
  }

  content(): FrameLocator {
    return this.page.frameLocator('iframe[name="pat"]')
  }

  firstNameInput(): Locator {
    return this.content().locator('#form_fname')
  }

  lastNameInput(): Locator {
    return this.content().locator('#form_lname')
  }

  dobInput(): Locator {
    return this.content().locator('#form_DOB')
  }

  sexSelect(): Locator {
    return this.content().locator('#form_sex')
  }

  createButton(): Locator {
    return this.content().locator('#create')
  }

  async fillRequiredFields(firstName: string, lastName: string, dob: string, sex: string): Promise<void> {
    await this.firstNameInput().fill(firstName)
    await this.lastNameInput().fill(lastName)
    await this.dobInput().fill(dob)
    await this.dobInput().press('Escape')
    await this.sexSelect().selectOption({ label: sex })
  }

  async submitCreate(): Promise<void> {
    await this.page.bringToFront()
    await this.createButton().click()
  }

  async confirmDuplicateCheck(): Promise<void> {
    const deadline = Date.now() + 30000
    while (Date.now() < deadline) {
      const dupFrame = this.page.frame({ url: /new_search_popup\.php/ })
      const button = await dupFrame?.$('#confirmCreate')
      if (button) {
        break
      }
      await this.page.waitForTimeout(200)
    }
    const patFrame = this.page.frame({ url: /new\/new\.php/ })
    await patFrame?.evaluate(() => {
      const restoreSession = (window.top as unknown as { restoreSession?: () => void })?.restoreSession
      if (typeof restoreSession === 'function') {
        restoreSession()
      }
      document.forms[0].submit()
    })
    await this.page.evaluate(() => {
      document.querySelectorAll('.dialogModal').forEach(el => el.remove())
      document.querySelectorAll('.modal-backdrop').forEach(el => el.remove())
    })
  }
}
