import { expect, test } from '@playwright/test'
import {
  PLATES,
  createRental,
  createRentalViaApi,
  createVehicle,
  currentCustomerId,
  period
} from './helpers'

/**
 * Nivel 5 de la piramide: E2E real. Navegador, frontend, nginx, cuatro
 * servicios, PostgreSQL y Kafka. Son las pruebas mas lentas y mas fragiles del
 * repositorio, y por eso hay pocas: solo recorridos de usuario completos que
 * ninguna capa inferior puede demostrar por si sola.
 */
test.describe('Flujo de renta', () => {
  test('la flota sembrada se muestra al entrar', async ({ page }) => {
    await page.goto('/')

    await expect(page.getByTestId('vehicles-table')).toBeVisible()
    for (const plate of Object.values(PLATES)) {
      await expect(page.getByTestId(`vehicle-row-${plate}`)).toBeVisible()
    }
  })

  test('la sesion genera y muestra un identificador de cliente', async ({ page }) => {
    await page.goto('/')

    const customerId = await page.getByTestId('customer-id').innerText()
    expect(customerId).toMatch(/^[0-9a-f-]{36}$/i)
  })

  test('el boton Rentar lleva al formulario con el vehiculo preseleccionado', async ({ page, request }) => {
    const vehicle = await createVehicle(request)

    await page.goto('/')
    await page.getByTestId(`rent-${vehicle.plate}`).click()

    await expect(page).toHaveURL(new RegExp(`/new\\?vehicleId=${vehicle.id}`))
    await expect(page.getByTestId('vehicle-select')).toHaveValue(vehicle.id)
  })

  test('solicitar una renta la crea en estado Pending con su total calculado', async ({ page, request }) => {
    const vehicle = await createVehicle(request, { vehicleClass: 'economy', dailyRate: 30 })

    await createRental(page, vehicle.plate, period(3))

    await expect(page.getByTestId('rental-status')).toHaveText('Pending')
    await expect(page.getByTestId('rental-days')).toHaveText('3')

    // Clase economy: multiplicador 1.0 sobre 30, tres dias sin descuento.
    await expect(page.getByTestId('rental-total')).toHaveText('90.00 USD')
  })

  test('el precio aplica el multiplicador de clase del servicio Pricing', async ({ page, request }) => {
    const vehicle = await createVehicle(request, { vehicleClass: 'suv', dailyRate: 60 })

    await createRental(page, vehicle.plate, period(2))

    // 60 * 1.35 (suv) = 81 al dia, dos dias = 162.
    await expect(page.getByTestId('rental-daily-rate')).toHaveText('81.00 USD')
    await expect(page.getByTestId('rental-total')).toHaveText('162.00 USD')
  })

  test('un periodo invertido es rechazado y el error se muestra al usuario', async ({ page }) => {
    const window = period(3)

    await page.goto('/new')
    await page.getByTestId('period-start').fill(window.end)
    await page.getByTestId('period-end').fill(window.start)
    await page.getByTestId('submit-rental').click()

    await expect(page.getByTestId('rental-error')).toBeVisible()
  })

  test('confirmar una renta cambia su estado y deshabilita el boton', async ({ page, request }) => {
    const vehicle = await createVehicle(request)
    await createRental(page, vehicle.plate, period(2))

    await page.getByTestId('confirm-rental').click()

    await expect(page.getByTestId('rental-status')).toHaveText('Confirmed')
    await expect(page.getByTestId('confirm-rental')).toBeDisabled()
  })

  test('cada transicion llega como notificacion publicada por Kafka', async ({ page, request }) => {
    const vehicle = await createVehicle(request)
    await createRental(page, vehicle.plate, period(2))

    // Crear la renta ya publica rental.requested; la pagina lo muestra en
    // cuanto Notifications lo consume. No es sincrono, y por eso se espera.
    await expect(page.getByTestId('notification-rental.requested')).toBeVisible()

    await page.getByTestId('confirm-rental').click()

    // La confirmacion viaja Rentals -> Kafka -> Notifications -> navegador.
    // Playwright reintenta la asercion hasta el timeout, que es exactamente la
    // forma correcta de esperar en un sistema por eventos.
    await expect(page.getByTestId('notification-rental.confirmed')).toBeVisible()
    await expect(page.getByTestId('notification-rental.confirmed')).toContainText('confirmed')
  })

  test('al confirmar, Fleet marca el vehiculo como rentado en la flota', async ({ page, request }) => {
    const vehicle = await createVehicle(request)
    await createRental(page, vehicle.plate, period(2))

    await page.getByTestId('confirm-rental').click()
    await expect(page.getByTestId('rental-status')).toHaveText('Confirmed')

    // Fleet consume el mismo evento y actualiza su propia base de datos.
    await expect(async () => {
      await page.goto('/')
      await expect(page.getByTestId(`vehicle-status-${vehicle.plate}`)).toHaveText('Rentado')
    }).toPass({ timeout: 30_000 })
  })

  test('cancelar una renta confirmada muestra el reembolso calculado', async ({ page, request }) => {
    const vehicle = await createVehicle(request, { vehicleClass: 'luxury', dailyRate: 130 })
    await createRental(page, vehicle.plate, period(2))

    await page.getByTestId('confirm-rental').click()
    await expect(page.getByTestId('rental-status')).toHaveText('Confirmed')

    await page.getByTestId('cancel-rental').click()

    await expect(page.getByTestId('rental-status')).toHaveText('Cancelled')
    // 130 * 1.8 (luxury) = 234 al dia, dos dias = 468. Se cancela con mas de
    // 48 horas de antelacion, asi que la politica devuelve el 100%.
    await expect(page.getByTestId('rental-refund')).toHaveText('468.00 USD')
    await expect(page.getByTestId('cancel-rental')).toBeDisabled()
  })

  test('un segundo alquiler solapado del mismo vehiculo es rechazado', async ({ page, request }) => {
    const vehicle = await createVehicle(request)
    const window = period(4)
    await createRental(page, vehicle.plate, window)

    await page.goto('/')
    await page.getByTestId(`rent-${vehicle.plate}`).click()
    await page.getByTestId('period-start').fill(window.start)
    await page.getByTestId('period-end').fill(window.end)
    await page.getByTestId('submit-rental').click()

    await expect(page.getByTestId('rental-error')).toContainText('overlapping')
  })

  test('la renta creada sobrevive a una recarga de pagina', async ({ page, request }) => {
    const vehicle = await createVehicle(request)
    const rentalId = await createRental(page, vehicle.plate, period(2))

    await page.reload()

    await expect(page.getByTestId('rental-id')).toHaveText(rentalId)
    await expect(page.getByTestId('rental-status')).toHaveText('Pending')
  })

  test('la reserva aparece en "Mis reservas" y se puede gestionar desde ahi', async ({ page, request }) => {
    const vehicle = await createVehicle(request)
    const rentalId = await createRental(page, vehicle.plate, period(2))

    await page.getByTestId('nav-rentals').click()

    await expect(page.getByTestId('rentals-table')).toBeVisible()
    await expect(page.getByTestId(`rental-row-status-${rentalId}`)).toHaveText('Pending')

    await page.getByTestId(`manage-${rentalId}`).click()
    await expect(page.getByTestId('rental-id')).toHaveText(rentalId)
  })

  test('prorrogar una reserva mueve el fin y recalcula el total', async ({ page, request }) => {
    const vehicle = await createVehicle(request, { vehicleClass: 'economy', dailyRate: 30 })
    const window = period(2)
    await createRental(page, vehicle.plate, window)

    await expect(page.getByTestId('rental-days')).toHaveText('2')
    await expect(page.getByTestId('rental-total')).toHaveText('60.00 USD')

    const longerEnd = new Date(`${window.end}T10:00:00.000Z`)
    longerEnd.setUTCDate(longerEnd.getUTCDate() + 3)
    await page.getByTestId('extend-date').fill(longerEnd.toISOString().slice(0, 10))
    await page.getByTestId('extend-rental').click()

    await expect(page.getByTestId('rental-days')).toHaveText('5')
    await expect(page.getByTestId('rental-total')).toHaveText('150.00 USD')
  })

  test('prorrogar hacia atras se rechaza y se muestra el error', async ({ page, request }) => {
    const vehicle = await createVehicle(request)
    const window = period(4)
    await createRental(page, vehicle.plate, window)

    const earlier = new Date(`${window.start}T10:00:00.000Z`)
    earlier.setUTCDate(earlier.getUTCDate() + 1)
    await page.getByTestId('extend-date').fill(earlier.toISOString().slice(0, 10))
    await page.getByTestId('extend-rental').click()

    await expect(page.getByTestId('rental-error')).toBeVisible()
    await expect(page.getByTestId('rental-days')).toHaveText('4')
  })

  test('la poliza de seguro se emite al solicitar y se activa al confirmar', async ({ page, request }) => {
    const vehicle = await createVehicle(request, { vehicleClass: 'economy', dailyRate: 30 })
    await createRental(page, vehicle.plate, period(3))

    // Insurances consume rental.requested y emite la poliza en borrador.
    await expect(page.getByTestId('policy-status')).toHaveText('Draft')
    await expect(page.getByTestId('policy-number')).toContainText('POL-')
    // standard: minimo 9/dia x 3 dias = 27, frente al 12 % de 90 = 10.80.
    await expect(page.getByTestId('policy-premium')).toHaveText('27.00 USD')

    await page.getByTestId('confirm-rental').click()

    await expect(page.getByTestId('policy-status')).toHaveText('Active')
  })

  test('cancelar con poca antelacion genera factura por la penalizacion', async ({ page, request }) => {
    const customerId = await currentCustomerId(page)
    const vehicle = await createVehicle(request, { vehicleClass: 'economy', dailyRate: 30 })

    // Menos de 24 h de antelacion: la politica devuelve el 25 %, asi que queda
    // un 75 % por cobrar y Billing emite factura.
    const rentalId = await createRentalViaApi(request, {
      customerId,
      vehicleId: vehicle.id,
      startInHours: 3,
      days: 2
    })

    await page.goto(`/rentals/${rentalId}`)
    await expect(page.getByTestId('invoice-empty')).toBeVisible()

    await page.getByTestId('confirm-rental').click()
    await expect(page.getByTestId('rental-status')).toHaveText('Confirmed')

    await page.getByTestId('cancel-rental').click()
    await expect(page.getByTestId('rental-status')).toHaveText('Cancelled')
    await expect(page.getByTestId('rental-refund')).toHaveText('15.00 USD')

    // 60 de total, 15 reembolsados -> 45 de penalizacion, mas 19 % de IVA.
    await expect(page.getByTestId('invoice-number')).toContainText('INV-')
    await expect(page.getByTestId('invoice-subtotal')).toHaveText('45.00 USD')
    await expect(page.getByTestId('invoice-tax')).toHaveText('8.55 USD')
    await expect(page.getByTestId('invoice-total')).toHaveText('53.55 USD')
    await expect(page.getByTestId('invoice-status')).toHaveText('Issued')
  })

  test('cancelar antes de confirmar no genera factura', async ({ page, request }) => {
    const vehicle = await createVehicle(request)
    await createRental(page, vehicle.plate, period(2))

    await page.getByTestId('cancel-rental').click()
    await expect(page.getByTestId('rental-status')).toHaveText('Cancelled')

    // Nunca hubo cargo, asi que no hay nada que facturar.
    await expect(page.getByTestId('invoice-empty')).toBeVisible()
  })
})
