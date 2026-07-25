import type { Page, Frame } from '@playwright/test'

export interface NewUserOptions {
  username: string
  password: string
  adminPassword: string
  firstName: string
  lastName: string
  accessGroup: string
  isProvider?: boolean
}

export class UserManagementPage {
  readonly page: Page

  constructor(page: Page) {
    this.page = page
  }

  async goto(): Promise<void> {
    await this.page.locator('.menuLabel', { hasText: 'Admin' }).first().click()
    await this.page.waitForTimeout(300)
    await this.page.getByText('Users', { exact: true }).click()
    await this.page.waitForTimeout(1000)
  }

  listFrame(): Frame | null {
    return this.page.frames().find(frame => frame.url().endsWith('usergroup_admin.php')) ?? null
  }

  addFormFrame(): Frame | null {
    return this.page.frames().find(frame => frame.url().includes('usergroup_admin_add.php')) ?? null
  }

  async openAddUserForm(): Promise<void> {
    await this.listFrame()?.getByText('Add User', { exact: true }).click()
    await this.page.waitForTimeout(1000)
  }

  async addUser(options: NewUserOptions): Promise<void> {
    const frame = this.addFormFrame()
    await frame?.locator('input[name="rumple"]').fill(options.username)
    await frame?.locator('input[name="stiltskin"]').fill(options.password)
    await frame?.locator('input[name="adminPass"]').fill(options.adminPassword)
    await frame?.locator('input[name="fname"]').fill(options.firstName)
    await frame?.locator('input[name="lname"]').fill(options.lastName)
    if (options.isProvider) {
      await frame?.locator('input[name="authorized"]').check()
      await frame?.locator('input[name="calendar"]').check()
    }
    await frame?.locator('select[name="access_group[]"]').selectOption([options.accessGroup])
    await frame?.locator('#form_save').click()
    await this.page.waitForTimeout(1500)
  }
}
