import { test, expect } from '@playwright/test'
import { LoginPage } from '../pages/LoginPage'
import { CalendarPage } from '../pages/CalendarPage'

test.describe('Patient scheduling', () => {
  test.beforeEach(async ({ page }) => {
    const login = new LoginPage(page)
    await login.goto()
    await login.loginAs('admin', 'pass')
  })

  test('booking a new appointment shows it on the calendar', async ({ page }) => {
    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm()
    await calendar.fillAppointment('Ada Lovelace', 'Follow-up', '09:00')
    await calendar.saveAppointment()
    await expect(calendar.content().locator('text=Ada Lovelace')).toBeVisible()
  })

  test('double booking the same provider slot is blocked', async ({ page }) => {
    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm()
    await calendar.fillAppointment('Grace Hopper', 'Intake', '10:00')
    await calendar.saveAppointment()
    await calendar.openNewAppointmentForm()
    await calendar.fillAppointment('Katherine Johnson', 'Intake', '10:00')
    await calendar.saveAppointment()
    await expect(calendar.content().locator('text=conflict')).toBeVisible()
  })

  test('canceling an appointment removes it from the day view', async ({ page }) => {
    const calendar = new CalendarPage(page)
    await calendar.goto()
    await calendar.openNewAppointmentForm()
    await calendar.fillAppointment('Margaret Hamilton', 'Follow-up', '11:00')
    await calendar.saveAppointment()
    await calendar.content().locator('text=Margaret Hamilton').click()
    await calendar.content().locator('#delete_button').click()
    await expect(calendar.content().locator('text=Margaret Hamilton')).toHaveCount(0)
  })
})
