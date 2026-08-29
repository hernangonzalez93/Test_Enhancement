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

  listNotifications: (customerId: string) =>
    request<Notification[]>(`/api/notifications?customerId=${customerId}`)
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

export function resetCustomer(): string {
  const id = crypto.randomUUID()
  localStorage.setItem(CUSTOMER_KEY, id)
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
