import http from 'k6/http'
import { check } from 'k6'

const BASE_URL = __ENV.BASE_URL
const SITE_ID = __ENV.SITE_ID
const ACCESS_TOKEN = __ENV.ACCESS_TOKEN
const TARGET_DATE = __ENV.TARGET_DATE
const TARGET_TIME = __ENV.TARGET_TIME
const VUS = Number(__ENV.VUS)
const ITERATIONS_PER_VU = Number(__ENV.ITERATIONS_PER_VU)

export const options = {
  insecureSkipTLSVerify: true,
  scenarios: {
    concurrent_booking: {
      executor: 'per-vu-iterations',
      vus: VUS,
      iterations: ITERATIONS_PER_VU,
      maxDuration: '60s'
    }
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<3000']
  }
}

function authHeaders() {
  return { headers: { Authorization: `Bearer ${ACCESS_TOKEN}`, 'Content-Type': 'application/json' } }
}

export function setup() {
  const payload = JSON.stringify({
    fname: 'LoadTest',
    lname: `Contention${Date.now()}`,
    DOB: '1985-05-05',
    sex: 'Female'
  })
  const response = http.post(`${BASE_URL}/apis/${SITE_ID}/api/patient`, payload, authHeaders())
  check(response, { 'fixture patient created': r => r.status === 200 || r.status === 201 })
  return { pid: response.json('data.pid') }
}

export default function (data) {
  const payload = JSON.stringify({
    pc_catid: '5',
    pc_title: 'Office Visit',
    pc_duration: '900',
    pc_hometext: 'k6 concurrent booking load test',
    pc_apptstatus: '-',
    pc_eventDate: TARGET_DATE,
    pc_startTime: TARGET_TIME,
    pc_facility: '1',
    pc_billing_location: '1',
    pc_aid: '1'
  })
  const response = http.post(`${BASE_URL}/apis/${SITE_ID}/api/patient/${data.pid}/appointment`, payload, authHeaders())
  check(response, {
    'booking request succeeds with 200': r => r.status === 200,
    'booking response has a numeric id': r => typeof r.json('id') === 'number'
  })
}

export function teardown(data) {
  const response = http.get(`${BASE_URL}/apis/${SITE_ID}/api/patient/${data.pid}/appointment`, authHeaders())
  const appointments = response.json()
  const matching = appointments.filter(a => a.pc_eventDate === TARGET_DATE && a.pc_startTime.startsWith(TARGET_TIME))
  const ids = matching.map(a => a.pc_eid)
  const uniqueIds = new Set(ids)
  const expectedCount = VUS * ITERATIONS_PER_VU
  check(null, {
    [`every one of the ${expectedCount} concurrent booking requests produced its own row (no lost writes under contention)`]: () => matching.length === expectedCount,
    'every booked appointment under contention got a distinct id (no id collision)': () => uniqueIds.size === ids.length
  })
}
