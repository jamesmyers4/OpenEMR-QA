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

  formsFrame(): Frame | null {
    return this.page.frame({ url: /encounter\/forms\.php/ })
  }

  soapFormFrame(): Frame | null {
    return this.page.frame({ url: /formname=soap/ })
  }

  async openSoapForm(): Promise<void> {
    const frame = this.formsFrame()
    await frame?.getByText('Clinical', { exact: true }).click()
    await this.page.waitForTimeout(300)
    await frame?.getByText('SOAP', { exact: true }).click()
    await this.page.waitForTimeout(1000)
  }

  async fillAndSubmitSoapNote(subjective: string, objective: string, assessment: string, plan: string): Promise<void> {
    const frame = this.soapFormFrame()
    await frame?.locator('textarea[name="subjective"]').fill(subjective)
    await frame?.locator('textarea[name="objective"]').fill(objective)
    await frame?.locator('textarea[name="assessment"]').fill(assessment)
    await frame?.locator('textarea[name="plan"]').fill(plan)
    await frame?.locator('button[type="submit"]').click()
    await this.page.waitForTimeout(1500)
  }

  async eSignLatestForm(password: string): Promise<void> {
    const frame = this.formsFrame()
    await frame?.getByText('eSign', { exact: true }).last().click()
    await this.page.waitForTimeout(800)
    await frame?.locator('#password').fill(password)
    await frame?.locator('#esign-sign-button-form').click()
    await this.page.waitForTimeout(1500)
  }

  feeSheetFrame(): Frame | null {
    return this.page.frame({ url: /formname=fee_sheet/ })
  }

  async openFeeSheet(): Promise<void> {
    await this.page.locator('.menuLabel', { hasText: 'Fees' }).first().click()
    await this.page.waitForTimeout(300)
    await this.page.getByText('Fee Sheet', { exact: true }).click()
    await this.page.waitForTimeout(1500)
  }

  async addEstablishedPatientDetailedVisitCode(): Promise<void> {
    const frame = this.feeSheetFrame()
    await frame?.getByRole('button', { name: 'Established Patient' }).click()
    await this.page.waitForTimeout(800)
    await frame?.evaluate(() => {
      const opt = document.querySelector('option[value="CPT4|99213|"]')
      const select = opt?.closest('select')
      if (select) {
        select.value = 'CPT4|99213|'
        select.dispatchEvent(new Event('change', { bubbles: true }))
      }
    })
    await this.page.waitForTimeout(300)
    await frame?.getByRole('button', { name: 'OK' }).click()
    await this.page.waitForTimeout(500)
  }

  async saveFeeSheet(): Promise<void> {
    const frame = this.feeSheetFrame()
    await frame?.locator('button.btn-save').first().click()
    await this.page.waitForTimeout(1500)
  }
}
