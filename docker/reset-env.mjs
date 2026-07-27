import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const __dirname = dirname(fileURLToPath(import.meta.url))
const composeFile = join(__dirname, 'docker-compose.yml')

const HEALTH_POLL_ATTEMPTS = 40
const HEALTH_POLL_INTERVAL_MS = 15000

function compose(args) {
  execFileSync('docker', ['compose', '-f', composeFile, ...args], { stdio: 'inherit' })
}

function containerId(service) {
  return execFileSync('docker', ['compose', '-f', composeFile, 'ps', '-q', service]).toString().trim()
}

function healthStatus(id) {
  try {
    return execFileSync('docker', ['inspect', '--format={{.State.Health.Status}}', id]).toString().trim()
  } catch {
    return 'starting'
  }
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms))
}

async function waitForHealthy(service) {
  const id = containerId(service)
  for (let attempt = 1; attempt <= HEALTH_POLL_ATTEMPTS; attempt++) {
    const status = healthStatus(id)
    if (status === 'healthy') {
      console.log(`${service} is healthy`)
      return
    }
    console.log(`Waiting for ${service}... (${status}, attempt ${attempt}/${HEALTH_POLL_ATTEMPTS})`)
    await delay(HEALTH_POLL_INTERVAL_MS)
  }
  throw new Error(`${service} never became healthy`)
}

console.log('Tearing down existing stack and dropping volumes (dbvolume, sitesvolume)...')
compose(['down', '-v'])

console.log('Starting a fresh stack...')
compose(['up', '-d'])

console.log('Waiting for mariadb and openemr to report healthy — first boot on a fresh volume can take several minutes, and openemr can report unhealthy for a while before flipping, which is normal.')
await waitForHealthy('mariadb')
await waitForHealthy('openemr')

console.log('Fresh environment ready with an empty patient_data table — DEMO_MODE does not work on this pinned production image tag (see CONTEXT.md), so there is no baseline data to reseed; the DB test project seeds its own known fixture patients as needed. No accumulated test fixtures remain.')
console.log('No manual browser steps are required first — LoginPage.loginAs() already dismisses the product-registration modal, and docker-compose.yml already sets the REST/FHIR/OAuth/portal globals via OPENEMR_SETTING_* env vars.')
