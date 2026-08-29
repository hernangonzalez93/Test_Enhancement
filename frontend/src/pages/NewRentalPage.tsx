import { useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import {
  api,
  currentCustomerId,
  fromDateInputValue,
  isoDaysFromNow,
  toDateInputValue,
  type Vehicle
} from '../api'
import { useEffect } from 'react'

export function NewRentalPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()

  const [vehicles, setVehicles] = useState<Vehicle[]>([])
  const [vehicleId, setVehicleId] = useState(params.get('vehicleId') ?? '')
  const [start, setStart] = useState(toDateInputValue(isoDaysFromNow(7)))
  const [end, setEnd] = useState(toDateInputValue(isoDaysFromNow(10)))
  const [licenseNumber, setLicenseNumber] = useState('LIC-12345')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.listVehicles().then(all => {
      setVehicles(all)
      if (!vehicleId && all.length > 0) {
        setVehicleId(all[0].id)
      }
    })
    // Solo al montar: el resto de cambios los controla el usuario.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      const rental = await api.createRental({
        customerId: currentCustomerId(),
        vehicleId,
        periodStart: fromDateInputValue(start),
        periodEnd: fromDateInputValue(end),
        licenseNumber,
        licenseExpiresOn: isoDaysFromNow(3650),
        extras: []
      })

      navigate(`/rentals/${rental.id}`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Error desconocido')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <section>
      <h2>Nueva renta</h2>

      <form onSubmit={submit} data-testid="rental-form">
        <label>
          Vehiculo
          <select
            data-testid="vehicle-select"
            value={vehicleId}
            onChange={event => setVehicleId(event.target.value)}
            required
          >
            <option value="">Selecciona un vehiculo</option>
            {vehicles.map(vehicle => (
              <option key={vehicle.id} value={vehicle.id}>
                {vehicle.model} ({vehicle.licensePlate})
              </option>
            ))}
          </select>
        </label>

        <label>
          Desde
          <input
            type="date"
            data-testid="period-start"
            value={start}
            onChange={event => setStart(event.target.value)}
            required
          />
        </label>

        <label>
          Hasta
          <input
            type="date"
            data-testid="period-end"
            value={end}
            onChange={event => setEnd(event.target.value)}
            required
          />
        </label>

        <label>
          Licencia
          <input
            data-testid="license-number"
            value={licenseNumber}
            onChange={event => setLicenseNumber(event.target.value)}
            required
          />
        </label>

        <button type="submit" data-testid="submit-rental" disabled={submitting}>
          {submitting ? 'Enviando...' : 'Solicitar renta'}
        </button>
      </form>

      {error && (
        <p className="error" data-testid="rental-error">
          {error}
        </p>
      )}
    </section>
  )
}
