import { expect, type APIRequestContext, type Page } from '@playwright/test'

/** Placas de la semilla de Fleet, usadas solo por las pruebas de solo lectura. */
export const PLATES = {
  economy: 'ECO-001',
  suv: 'SUV-002',
  luxury: 'LUX-003',
  compact: 'CMP-004',
  economy2: 'ECO-005',
  compact2: 'CMP-006',
  suv2: 'SUV-007',
  luxury2: 'LUX-008'
} as const

export interface TestVehicle {
  id: string
  plate: string
  dailyRate: number
}

/**
 * Cada prueba que llega a crear una renta usa SU PROPIO vehiculo.
 *
 * Confirmar una renta hace que Fleet marque el vehiculo como rentado, y ese
 * cambio persiste en la base de datos. Si las pruebas se repartieran la flota
 * sembrada, la suite pasaria la primera vez y fallaria la segunda: el clasico
 * fallo por estado compartido. Crear el vehiculo dentro de la prueba la hace
 * repetible sin tener que limpiar la base entre ejecuciones.
 */
export async function createVehicle(
  request: APIRequestContext,
  options: { vehicleClass?: string; dailyRate?: number } = {}
): Promise<TestVehicle> {
  const vehicleClass = options.vehicleClass ?? 'economy'
  const dailyRate = options.dailyRate ?? 30
  const plate = `E2E-${Math.random().toString(36).slice(2, 8).toUpperCase()}`

  const response = await request.post('/api/vehicles', {
    data: {
      model: `Prueba ${plate}`,
      vehicleClass,
      licensePlate: plate,
      dailyRate,
      currency: 'USD'
    }
  })

  expect(response.status()).toBe(201)
  const body = await response.json()

  return { id: body.id, plate: body.licensePlate, dailyRate }
}

/** Ventana de fechas futura y acotada, dentro de la vigencia de la licencia. */
export function period(days = 3): { start: string; end: string } {
  const start = new Date()
  start.setUTCDate(start.getUTCDate() + 30 + Math.floor(Math.random() * 200))

  const end = new Date(start)
  end.setUTCDate(end.getUTCDate() + days)

  return { start: isoDate(start), end: isoDate(end) }
}

function isoDate(date: Date): string {
  return date.toISOString().slice(0, 10)
}

/** Rellena y envia el formulario de nueva renta. Devuelve el id de la renta. */
export async function createRental(
  page: Page,
  plate: string,
  window: { start: string; end: string }
): Promise<string> {
  await page.goto('/')
  await page.getByTestId(`rent-${plate}`).click()

  await expect(page.getByTestId('rental-form')).toBeVisible()
  await page.getByTestId('period-start').fill(window.start)
  await page.getByTestId('period-end').fill(window.end)
  await page.getByTestId('submit-rental').click()

  await expect(page.getByTestId('rental-status')).toBeVisible()
  return (await page.getByTestId('rental-id').innerText()).trim()
}

/** Identificador de cliente que el navegador guarda en localStorage. */
export async function currentCustomerId(page: Page): Promise<string> {
  await page.goto('/')
  return (await page.getByTestId('customer-id').innerText()).trim()
}

/**
 * Crea una renta llamando directamente a la API.
 *
 * El formulario solo tiene granularidad de dia, y hay escenarios que necesitan
 * una hora exacta: por ejemplo cancelar con menos de 24 h de antelacion para que
 * quede penalizacion y por tanto factura. Aqui se fija el instante al segundo.
 */
export async function createRentalViaApi(
  request: APIRequestContext,
  options: { customerId: string; vehicleId: string; startInHours: number; days: number }
): Promise<string> {
  const start = new Date(Date.now() + options.startInHours * 3600_000)
  const end = new Date(start.getTime() + options.days * 24 * 3600_000)
  const licenseExpiry = new Date(Date.now() + 3 * 365 * 24 * 3600_000)

  const response = await request.post('/api/rentals', {
    data: {
      customerId: options.customerId,
      vehicleId: options.vehicleId,
      periodStart: start.toISOString(),
      periodEnd: end.toISOString(),
      licenseNumber: 'LIC-12345',
      licenseExpiresOn: licenseExpiry.toISOString(),
      extras: []
    }
  })

  expect(response.status()).toBe(201)
  return (await response.json()).id
}
