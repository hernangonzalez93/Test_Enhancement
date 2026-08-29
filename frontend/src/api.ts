export interface Vehicle {
  id: string
  model: string
  vehicleClass: string
  licensePlate: string
  dailyRate: number
  currency: string
  available: boolean
}

export interface Rental {
  id: string
  customerId: string
  vehicleId: string
  periodStart: string
  periodEnd: string
  totalDays: number
  status: string
  dailyRate: number
  estimatedTotal: number
  finalTotal: number | null
  refundAmount: number | null
  currency: string
  lateDays: number
}

export interface Notification {
  id: string
  rentalId: string
  customerId: string
  eventType: string
  message: string
  createdAt: string
}

export interface Policy {
  number: string
  rentalId: string
  customerId: string
  coverage: string
  premium: number
  currency: string
  validFrom: string
  validTo: string
  status: string
  updatedAt: string
}

export interface InvoiceLine {
  concept: string
  amount: number
}

export interface Invoice {
  id: string
  number: string
  rentalId: string
  customerId: string
  status: string
  currency: string
  subtotal: number
  tax: number
  total: number
  lines: InvoiceLine[]
  createdAt: string
  issuedAt: string | null
  paidAt: string | null
}

export interface CreateRentalRequest {
  customerId: string
  vehicleId: string
  periodStart: string
  periodEnd: string
  licenseNumber: string
  licenseExpiresOn: string
  extras: string[]
}

/** Error de negocio devuelto por la API como problem+json. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string
  ) {
    super(message)
  }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) }
  })

  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new ApiError(
      problem?.detail ?? `Request failed with ${response.status}`,
      response.status,
      problem?.errorCode ?? problem?.title ?? 'unknown'
    )
  }

  if (response.status === 204) {
    return undefined as T
  }

  return (await response.json()) as T
}

export const api = {
  listVehicles: () => request<Vehicle[]>('/api/vehicles'),

  getRental: (id: string) => request<Rental>(`/api/rentals/${id}`),

  listRentals: (customerId: string) =>
    request<Rental[]>(`/api/rentals?customerId=${customerId}`),

  createRental: (body: CreateRentalRequest) =>
    request<Rental>('/api/rentals', { method: 'POST', body: JSON.stringify(body) }),

  confirmRental: (id: string) =>
    request<Rental>(`/api/rentals/${id}/confirm`, { method: 'POST' }),

  cancelRental: (id: string) =>
    request<Rental>(`/api/rentals/${id}/cancel`, { method: 'POST' }),

  extendRental: (id: string, periodEnd: string) =>
    request<Rental>(`/api/rentals/${id}/extend`, {
      method: 'POST',
      body: JSON.stringify({ periodEnd })
    }),

  startRental: (id: string) =>
    request<Rental>(`/api/rentals/${id}/start`, { method: 'POST' }),

  completeRental: (id: string) =>
    request<Rental>(`/api/rentals/${id}/complete`, { method: 'POST' }),

  listNotifications: (customerId: string) =>
    request<Notification[]>(`/api/notifications?customerId=${customerId}`),

  listPolicies: (rentalId: string) =>
    request<Policy[]>(`/api/policies?rentalId=${rentalId}`),

  listInvoices: (rentalId: string) =>
    request<Invoice[]>(`/api/invoices?rentalId=${rentalId}`)
}

/** El cliente se guarda en el navegador para simular una sesion sin login. */
const CUSTOMER_KEY = 'testenforce.customerId'

export function currentCustomerId(): string {
  let id = localStorage.getItem(CUSTOMER_KEY)
  if (!id) {
    id = crypto.randomUUID()
    localStorage.setItem(CUSTOMER_KEY, id)
  }

  return id
}

export function isoDaysFromNow(days: number): string {
  const date = new Date()
  date.setUTCDate(date.getUTCDate() + days)
  date.setUTCHours(10, 0, 0, 0)
  return date.toISOString()
}

export function toDateInputValue(iso: string): string {
  return iso.slice(0, 10)
}

export function fromDateInputValue(value: string): string {
  return new Date(`${value}T10:00:00.000Z`).toISOString()
}

export function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString('es-ES', {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC'
  })
}

export function formatMoney(amount: number, currency: string): string {
  return `${amount.toFixed(2)} ${currency}`
}
