import type { Page, Locator, Frame } from '@playwright/test'

export class BillingPaymentPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.page.locator('.menuLabel', { hasText: 'Fees' }).first().click()
    await this.page.waitForTimeout(300)
    await this.page.locator('.menuLabel', { hasText: 'Payment' }).first().click()
  }

  content(): Frame | null {
    return this.page.frame({ url: /front_payment\.php/ })
  }

  paymentMethodSelect(): Locator | undefined {
    return this.content()?.locator('#form_method')
  }

  amountInput(): Locator | undefined {
    return this.content()?.locator('#paying_1')
  }

  saveButton(): Locator | undefined {
    return this.content()?.locator('button[name="form_save"]')
  }

  async applyPayment(method: string, amount: string): Promise<void> {
    await this.paymentMethodSelect()?.selectOption({ value: method })
    await this.amountInput()?.fill(amount)
    await this.saveButton()?.click()
  }
}
