import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, type Vehicle } from '../api'

export function VehiclesPage() {
  const [vehicles, setVehicles] = useState<Vehicle[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .listVehicles()
      .then(setVehicles)
      .catch((cause: Error) => setError(cause.message))
  }, [])

  if (error) {
    return <p data-testid="vehicles-error">No se pudo cargar la flota: {error}</p>
  }

  if (!vehicles) {
    return <p data-testid="vehicles-loading">Cargando flota...</p>
  }

  return (
    <section>
      <h2>Flota disponible</h2>

      <table data-testid="vehicles-table">
        <thead>
          <tr>
            <th>Modelo</th>
            <th>Clase</th>
            <th>Placa</th>
            <th>Tarifa base</th>
            <th>Estado</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {vehicles.map(vehicle => (
            <tr key={vehicle.id} data-testid={`vehicle-row-${vehicle.licensePlate}`}>
              <td>{vehicle.model}</td>
              <td>{vehicle.vehicleClass}</td>
              <td>{vehicle.licensePlate}</td>
              <td>
                {vehicle.dailyRate.toFixed(2)} {vehicle.currency}
              </td>
              <td data-testid={`vehicle-status-${vehicle.licensePlate}`}>
                {vehicle.available ? 'Disponible' : 'Rentado'}
              </td>
              <td>
                {vehicle.available && (
                  <Link
                    className="button"
                    data-testid={`rent-${vehicle.licensePlate}`}
                    to={`/new?vehicleId=${vehicle.id}`}
                  >
                    Rentar
                  </Link>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
