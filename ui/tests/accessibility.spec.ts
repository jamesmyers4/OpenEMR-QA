import { test, expect } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { LoginPage } from '../pages/LoginPage'
import { CalendarPage } from '../pages/CalendarPage'
import { PatientRegistrationPage } from '../pages/PatientRegistrationPage'

test.describe('Accessibility', () => {
  test('login page has exactly one known critical violation: the language select has no accessible name', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    const results = await new AxeBuilder({ page }).analyze()
    const criticalIds = results.violations.filter(v => v.impact === 'critical').map(v => v.id).sort()
    expect(criticalIds, JSON.stringify(results.violations, null, 2)).toEqual(['select-name'])
  })

  test('calendar day view has no critical violations', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
    const calendar = new CalendarPage(page)
    await calendar.goto()
    const results = await new AxeBuilder({ page }).analyze()
    const critical = results.violations.filter(v => v.impact === 'critical')
    expect(critical, JSON.stringify(critical, null, 2)).toEqual([])
  })

  test('patient registration form has exactly one known critical violation: invalid aria-controls on the collapsible sections', async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
    const registration = new PatientRegistrationPage(page)
    await registration.goto()
    const results = await new AxeBuilder({ page }).analyze()
    const criticalIds = results.violations.filter(v => v.impact === 'critical').map(v => v.id).sort()
    expect(criticalIds, JSON.stringify(results.violations, null, 2)).toEqual(['aria-valid-attr-value'])
  })
})
