import type { Page, Frame } from '@playwright/test'

export class DuplicatePatientsPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.clickUntilNextIsVisible('Admin', 'Patients')
    await this.clickUntilNextIsVisible('Patients', 'Manage Duplicates')
    await this.page.locator('.menuLabel', { hasText: 'Manage Duplicates' }).first().click()
  }

  private async clickUntilNextIsVisible(toClickText: string, revealsText: string): Promise<void> {
    const revealed = this.page.locator('.menuLabel', { hasText: revealsText }).first()
    const deadline = Date.now() + 15000
    while (Date.now() < deadline) {
      await this.page.locator('.menuLabel', { hasText: toClickText }).first().click()
      const becameVisible = await revealed.waitFor({ state: 'visible', timeout: 2000 }).then(() => true).catch(() => false)
      if (becameVisible) {
        return
      }
    }
    throw new Error(`'${revealsText}' never became visible after repeatedly clicking '${toClickText}'`)
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
