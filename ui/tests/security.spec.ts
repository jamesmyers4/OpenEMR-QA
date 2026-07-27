import { test, expect } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'

test.describe('Security', () => {
  test.describe.configure({ retries: 2 })

  test('an XSS-shaped patient last name is escaped, not executed, on the demographics page', async ({ page }) => {
    page.on('dialog', dialog => dialog.accept())
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
    const registration = new PatientRegistrationPage(page)
    await registration.goto()
    const uniqueId = Date.now()
    const xssMarker = `XSS${uniqueId}`
    const xssPayload = `<img src=x onerror="window.__xssFired=true">${xssMarker}`
    await registration.fillRequiredFields('Playwright', xssPayload, '1985-05-15', 'Female')
    await registration.submitCreate()
    await registration.confirmDuplicateCheck()
    await expect.poll(() => page.frames().some(frame => frame.url().includes('demographics.php')), { timeout: 10000 }).toBe(true)
    const demographicsFrame = page.frames().find(frame => /summary\/demographics(_save)?\.php/.test(frame.url()))
    await expect.poll(() => demographicsFrame?.evaluate(() => document.body.innerText)).toContain(xssMarker)
    const xssFired = await page.evaluate(() => (window as unknown as { __xssFired?: boolean }).__xssFired)
    expect(xssFired).toBeUndefined()
    const bodyHtml = await demographicsFrame?.evaluate(() => document.body.innerHTML)
    expect(bodyHtml).not.toContain('<img src=x onerror=')
  })

  test('the session cookie is regenerated on login, not fixed from the pre-login session', async ({ page, context }) => {
    const login = new LoginPage(page)
    await login.goto()
    const beforeLoginCookie = (await context.cookies()).find(c => c.name === 'OpenEMR')?.value
    expect(beforeLoginCookie).toBeTruthy()
    await login.loginAs('admin', 'pass')
    const afterLoginCookie = (await context.cookies()).find(c => c.name === 'OpenEMR')?.value
    expect(afterLoginCookie).toBeTruthy()
    expect(afterLoginCookie).not.toBe(beforeLoginCookie)
  })
})
