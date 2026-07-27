import { execFileSync, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'

const __dirname = dirname(fileURLToPath(import.meta.url))
const composeFile = join(__dirname, '..', 'docker', 'docker-compose.yml')

const BASE_URL = process.env.BASE_URL ?? 'https://localhost:9300'
const SITE_ID = process.env.SITE_ID ?? 'default'
const VUS = process.env.VUS ?? '20'
const ITERATIONS_PER_VU = process.env.ITERATIONS_PER_VU ?? '1'
const TARGET_TIME = process.env.TARGET_TIME ?? '09:00'
const SCOPE = 'openid api:oemr user/patient.read user/patient.write user/appointment.read user/appointment.write'

function targetDate() {
  const inThirtyDays = new Date(Date.now() + 30 * 24 * 60 * 60 * 1000)
  return inThirtyDays.toISOString().slice(0, 10)
}

async function registerClient() {
  const response = await fetch(`${BASE_URL}/oauth2/${SITE_ID}/registration`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      application_type: 'private',
      client_name: 'OpenEmr.Load.Tests',
      redirect_uris: ['https://localhost/apis'],
      token_endpoint_auth_method: 'client_secret_post',
      grant_types: ['password'],
      scope: SCOPE
    })
  })
  if (!response.ok) {
    throw new Error(`client registration failed: ${response.status} ${await response.text()}`)
  }
  const body = await response.json()
  return { clientId: body.client_id, clientSecret: body.client_secret }
}

function enableClient(clientId) {
  execFileSync('docker', [
    'compose', '-f', composeFile, 'exec', '-T', 'mariadb',
    'mariadb', '-uopenemr', '-popenemr', 'openemr',
    '-e', `UPDATE oauth_clients SET is_enabled = 1 WHERE client_id = '${clientId}'`
  ], { stdio: 'inherit' })
}

async function requestToken(clientId, clientSecret) {
  const form = new URLSearchParams({
    grant_type: 'password',
    client_id: clientId,
    client_secret: clientSecret,
    username: 'admin',
    password: 'pass',
    scope: SCOPE,
    user_role: 'users'
  })
  const response = await fetch(`${BASE_URL}/oauth2/${SITE_ID}/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: form
  })
  if (!response.ok) {
    throw new Error(`token request failed: ${response.status} ${await response.text()}`)
  }
  const body = await response.json()
  return body.access_token
}

const { clientId, clientSecret } = await registerClient()
enableClient(clientId)
const accessToken = await requestToken(clientId, clientSecret)

const k6Result = spawnSync('k6', ['run', join(__dirname, 'appointment-booking.js')], {
  stdio: 'inherit',
  env: {
    ...process.env,
    BASE_URL,
    SITE_ID,
    ACCESS_TOKEN: accessToken,
    TARGET_DATE: targetDate(),
    TARGET_TIME,
    VUS,
    ITERATIONS_PER_VU
  }
})

process.exit(k6Result.status ?? 1)
