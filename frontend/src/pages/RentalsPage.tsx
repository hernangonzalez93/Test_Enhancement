import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, currentCustomerId, formatDate, formatMoney, type Rental } from '../api'

/** Estados que todavia admiten alguna accion del usuario. */
const ACTIONABLE = ['Pending', 'Confirmed', 'Active']

export function RentalsPage() {
  const [rentals, setRentals] = useState<Rental[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .listRentals(currentCustomerId())
      .then(setRentals)
      .catch((cause: Error) => setError(cause.message))
  }, [])

  if (error) {
    return <p data-testid="rentals-error">No se pudieron cargar las reservas: {error}</p>
  }

  if (!rentals) {
    return <p data-testid="rentals-loading">Cargando reservas...</p>
  }

  if (rentals.length === 0) {
    return (
      <section>
        <h2>Mis reservas</h2>
        <p data-testid="rentals-empty">
          Todavia no tienes reservas. <Link to="/">Elige un vehiculo</Link> para empezar.
        </p>
      </section>
    )
  }

  return (
    <section>
      <h2>Mis reservas</h2>

      <table data-testid="rentals-table">
        <thead>
          <tr>
            <th>Periodo</th>
            <th>Dias</th>
            <th>Estado</th>
            <th>Total</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {rentals.map(rental => (
            <tr key={rental.id} data-testid={`rental-row-${rental.id}`}>
              <td>
                {formatDate(rental.periodStart)} → {formatDate(rental.periodEnd)}
              </td>
              <td>{rental.totalDays}</td>
              <td data-testid={`rental-row-status-${rental.id}`}>{rental.status}</td>
              <td>{formatMoney(rental.estimatedTotal, rental.currency)}</td>
              <td>
                <Link className="button" data-testid={`manage-${rental.id}`} to={`/rentals/${rental.id}`}>
                  {ACTIONABLE.includes(rental.status) ? 'Gestionar' : 'Ver'}
                </Link>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
