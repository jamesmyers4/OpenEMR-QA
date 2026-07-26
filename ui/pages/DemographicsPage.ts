import type { Page, Frame } from '@playwright/test'

export class DemographicsPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  dashboardFrame(): Frame | null {
    return this.page.frames().find(frame => /summary\/demographics(_save)?\.php/.test(frame.url())) ?? null
  }

  editFrame(): Frame | null {
    return this.page.frames().find(frame => frame.url().includes('demographics_full.php')) ?? null
  }

  credentialsFrame(): Frame | null {
    return this.page.frames().find(frame => frame.url().includes('create_portallogin.php')) ?? null
  }

  async openEditForm(): Promise<void> {
    await this.dashboardFrame()?.locator('a[href="demographics_full.php"]').click()
  }

  async setContactEmail(email: string): Promise<void> {
    const frame = this.editFrame()
    await frame?.locator('#header_tab_Contact').click()
    await this.page.waitForTimeout(800)
    await frame?.locator('#form_email').fill(email)
  }

  async enablePatientPortal(): Promise<void> {
    const frame = this.editFrame()
    await frame?.locator('#header_tab_Choices').click()
    await this.page.waitForTimeout(800)
    await frame?.locator('#form_allow_patient_portal').selectOption('YES')
  }

  async save(): Promise<void> {
    await this.editFrame()?.locator('#submit_btn').click()
  }

  async expandPortalCard(): Promise<void> {
    await this.dashboardFrame()?.getByText('Patient Portal / API Access').click()
  }

  async portalCardExpandedClass(): Promise<string | null | undefined> {
    return this.dashboardFrame()?.locator('#patient_portal_expand').getAttribute('class')
  }

  async openCreateCredentials(): Promise<void> {
    await this.dashboardFrame()?.locator('a.small_modal', { hasText: 'Create' }).click()
  }

  async readGeneratedCredentials(): Promise<{ username: string, password: string }> {
    const frame = this.credentialsFrame()!
    const username = await frame.locator('#login_uname').inputValue()
    const password = await frame.locator('#pwd').inputValue()
    return { username, password }
  }

  async saveCredentials(): Promise<void> {
    await this.credentialsFrame()?.locator('a', { hasText: 'Save' }).click()
  }
}
