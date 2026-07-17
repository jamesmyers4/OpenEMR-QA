import { test, expect } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'

test.describe('Authentication', () => {
  test('valid admin credentials land on the main dashboard', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
    await expect(page).toHaveURL(/main_screen\.php/)
  })

  test('invalid credentials show an error and stay on login', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'wrong-password')
    await expect(page.locator('#loginfailure')).toBeVisible()
    await expect(page).toHaveURL(/login\.php/)
  })

  test('empty password is rejected client side', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', '')
    await expect(page).toHaveURL(/login\.php/)
  })

  test('logout returns user to the login screen', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
    await page.locator('a[href*="logout.php"]').click()
    await expect(page).toHaveURL(/login\.php/)
  })
})
