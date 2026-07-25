import { test, expect, type Page } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'
import { EncounterPage } from '../pages/EncounterPage'
import { UserManagementPage } from '../pages/UserManagementPage'

async function createNonAdminUser(userManagement: UserManagementPage, accessGroup: string, isProvider: boolean): Promise<{ username: string, password: string }> {
  const uniqueId = Date.now()
  const username = `${accessGroup.replace(/\s/g, '')}${uniqueId}`.toLowerCase()
  const password = 'TestPass123!'
  await userManagement.goto()
  await expect.poll(() => !!userManagement.listFrame(), { timeout: 10000 }).toBe(true)
  await userManagement.openAddUserForm()
  await expect.poll(() => !!userManagement.addFormFrame(), { timeout: 10000 }).toBe(true)
  await userManagement.addUser({ username, password, adminPassword: 'pass', firstName: 'Test', lastName: accessGroup, accessGroup, isProvider })
  return { username, password }
}

async function createOwnPatient(page: Page, registration: PatientRegistrationPage): Promise<void> {
  const lastName = `Rbac${Date.now()}`
  await registration.goto()
  await registration.fillRequiredFields('Rbac', lastName, '1990-06-15', 'Male')
  await registration.submitCreate()
  await registration.confirmDuplicateCheck()
  await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
}

test.describe('Role-based access control in the UI', () => {
  test.describe.configure({ retries: 2 })

  test.beforeEach(async ({ page }) => {
    page.on('dialog', dialog => dialog.accept())
  })

  test('a front-desk user has no Admin menu and cannot select a clinical visit category', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')

    const userManagement = new UserManagementPage(page)
    const { username, password } = await createNonAdminUser(userManagement, 'Front Office', false)

    await login.logout()
    await expect(page).toHaveURL(/login\.php/)
    await login.loginAs(username, password)

    await expect(page.locator('.menuLabel', { hasText: 'Admin' })).toHaveCount(0)

    const registration = new PatientRegistrationPage(page)
    const encounter = new EncounterPage(page)
    await createOwnPatient(page, registration)
    await encounter.goto()
    await expect.poll(() => !!encounter.visitFormFrame(), { timeout: 10000 }).toBe(true)

    const categoryOptionCount = await encounter.visitFormFrame()!.locator('#pc_catid option').count()
    expect(categoryOptionCount, 'front-desk should only see the unselectable placeholder option, no real visit categories').toBe(1)
  })

  test('a provider (Clinicians) user has no Admin menu but can select a real clinical visit category', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')

    const userManagement = new UserManagementPage(page)
    const { username, password } = await createNonAdminUser(userManagement, 'Clinicians', true)

    await login.logout()
    await expect(page).toHaveURL(/login\.php/)
    await login.loginAs(username, password)

    await expect(page.locator('.menuLabel', { hasText: 'Admin' })).toHaveCount(0)

    const registration = new PatientRegistrationPage(page)
    const encounter = new EncounterPage(page)
    await createOwnPatient(page, registration)
    await encounter.goto()
    await expect.poll(() => !!encounter.visitFormFrame(), { timeout: 10000 }).toBe(true)

    const categoryOptionCount = await encounter.visitFormFrame()!.locator('#pc_catid option').count()
    expect(categoryOptionCount, 'a provider should see real visit categories, not just the placeholder').toBeGreaterThan(1)
  })
})
