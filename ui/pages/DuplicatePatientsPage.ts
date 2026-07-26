import type { Page, Frame } from '@playwright/test'

export class DuplicatePatientsPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.page.getByRole('button', { name: 'Admin', exact: true }).click()
    await this.page.waitForTimeout(500)
    await this.page.getByRole('button', { name: 'Patients', exact: true }).click()
    await this.page.waitForTimeout(500)
    const patientsSection = this.page.locator('.menuSection', { has: this.page.getByRole('button', { name: 'Patients', exact: true }) }).first()
    await patientsSection.locator('.menuLabel', { hasText: 'Manage Duplicates' }).click()
  }

  managerFrame(): Frame | null {
    return this.page.frames().find(frame => frame.url().includes('manage_dup_patients.php')) ?? null
  }

  mergeFrame(): Frame | null {
    return this.page.frames().find(frame => frame.url().includes('merge_patients.php')) ?? null
  }

  mergeCandidateRow(lastName: string) {
    const frame = this.managerFrame()!
    return frame.locator('tr').filter({ hasText: lastName }).filter({ has: frame.locator('option[value="MD"]') })
  }

  async mergeAndDiscard(lastName: string): Promise<void> {
    await this.mergeCandidateRow(lastName).locator('select').selectOption('MD')
  }

  async confirmMerge(): Promise<void> {
    await this.mergeFrame()?.locator('button[name="form_submit"][value="merge"]').click()
  }

  async returnToDuplicateManager(): Promise<void> {
    await this.mergeFrame()?.locator("input[value='Go to Duplicate Manager']").click()
  }
}
