import type { Page, Locator, Frame } from '@playwright/test'

export class EncounterPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.page.locator('.menuLabel', { hasText: 'Patient' }).first().click()
    await this.page.waitForTimeout(300)
    await this.page.locator('.menuLabel', { hasText: 'Visits' }).first().click()
    await this.page.waitForTimeout(300)
    await this.page.getByText('Create Visit', { exact: true }).click()
  }

  visitFormFrame(): Frame | null {
    return this.page.frame({ url: /forms\/newpatient\/new\.php/ })
  }

  visitCategorySelect(): Locator | undefined {
    return this.visitFormFrame()?.locator('#pc_catid')
  }

  reasonInput(): Locator | undefined {
    return this.visitFormFrame()?.locator('#reason')
  }

  saveButton(): Locator | undefined {
    return this.visitFormFrame()?.locator('#saveEncounter')
  }

  async createVisit(reason: string): Promise<void> {
    await this.visitCategorySelect()?.selectOption({ value: '5' })
    await this.reasonInput()?.fill(reason)
    await this.saveButton()?.click()
  }
}
