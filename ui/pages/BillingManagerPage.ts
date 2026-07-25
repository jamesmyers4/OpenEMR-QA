import type { Page, Locator, Frame, Download } from '@playwright/test'

export class BillingManagerPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.page.locator('.menuLabel', { hasText: 'Fees' }).first().click()
    await this.page.waitForTimeout(300)
    await this.page.getByText('Billing Manager', { exact: true }).click()
    await this.page.waitForTimeout(1500)
  }

  content(): Frame | null {
    return this.page.frames().find(frame => frame.url().includes('billing_report.php')) ?? null
  }

  rowFor(patientLastName: string): Locator | undefined {
    return this.content()?.locator('tr', { hasText: patientLastName })
  }

  async selectForBilling(patientLastName: string): Promise<void> {
    await this.rowFor(patientLastName)?.locator('input[type="checkbox"]').first().check()
  }

  async generateCms1500Pdf(): Promise<Download> {
    const frame = this.content()
    await frame?.locator('button[name="bn_process_hcfa_support"]').click()
    await this.page.waitForTimeout(500)
    const downloadPromise = this.page.waitForEvent('download')
    await frame?.locator('button[name="bn_process_hcfa"]').click()
    await this.page.waitForTimeout(1000)
    const confirmDialog = frame?.locator('#confirmDialog')
    if (await confirmDialog?.isVisible()) {
      await confirmDialog?.locator('.btn-continue').click()
    }
    return downloadPromise
  }
}
