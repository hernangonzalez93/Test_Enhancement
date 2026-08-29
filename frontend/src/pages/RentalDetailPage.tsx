import { useCallback, useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import { api, currentCustomerId, type Notification, type Rental } from '../api'

export function RentalDetailPage() {
  const { id = '' } = useParams()
  const [rental, setRental] = useState<Rental | null>(null)
  const [notifications, setNotifications] = useState<Notification[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      setRental(await api.getRental(id))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Error desconocido')
    }
  }, [id])

  useEffect(() => {
    void load()
  }, [load])

  // Las notificaciones llegan por Kafka, de forma asincrona: el cliente
  // consulta cada segundo hasta verlas. Es exactamente el comportamiento que
  // las pruebas E2E deben esperar en vez de asumir inmediatez.
  useEffect(() => {
    const customerId = currentCustomerId()
    const poll = () => {
      api
        .listNotifications(customerId)
        .then(all => setNotifications(all.filter(n => n.rentalId === id)))
        .catch(() => undefined)
    }

    poll()
    const handle = setInterval(poll, 1000)
    return () => clearInterval(handle)
  }, [id])

  async function act(action: 'confirm' | 'cancel') {
    setBusy(true)
    setError(null)

    try {
      setRental(action === 'confirm' ? await api.confirmRental(id) : await api.cancelRental(id))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Error desconocido')
    } finally {
      setBusy(false)
    }
  }

  if (!rental) {
    return <p data-testid="rental-loading">Cargando renta...</p>
  }

  return (
    <section>
      <h2>Renta</h2>

      <dl className="rental-detail">
        <dt>Id</dt>
        <dd data-testid="rental-id">{rental.id}</dd>

        <dt>Estado</dt>
        <dd data-testid="rental-status">{rental.status}</dd>

        <dt>Dias</dt>
        <dd data-testid="rental-days">{rental.totalDays}</dd>

        <dt>Tarifa diaria</dt>
        <dd data-testid="rental-daily-rate">
          {rental.dailyRate.toFixed(2)} {rental.currency}
        </dd>

        <dt>Total estimado</dt>
        <dd data-testid="rental-total">
          {rental.estimatedTotal.toFixed(2)} {rental.currency}
        </dd>

        {rental.refundAmount !== null && (
          <>
            <dt>Reembolso</dt>
            <dd data-testid="rental-refund">
              {rental.refundAmount.toFixed(2)} {rental.currency}
            </dd>
          </>
        )}
      </dl>

      <div className="actions">
        <button
          data-testid="confirm-rental"
          onClick={() => act('confirm')}
          disabled={busy || rental.status !== 'Pending'}
        >
          Confirmar
        </button>
        <button
          data-testid="cancel-rental"
          onClick={() => act('cancel')}
          disabled={busy || (rental.status !== 'Pending' && rental.status !== 'Confirmed')}
        >
          Cancelar
        </button>
      </div>

      {error && (
        <p className="error" data-testid="rental-error">
          {error}
        </p>
      )}

      <h3>Notificaciones</h3>
      <ul data-testid="notifications">
        {notifications.map(notification => (
          <li key={notification.id} data-testid={`notification-${notification.eventType}`}>
            <strong>{notification.eventType}</strong> {notification.message}
          </li>
        ))}
      </ul>
      {notifications.length === 0 && (
        <p data-testid="notifications-empty">Aun no hay notificaciones para esta renta.</p>
      )}
    </section>
  )
}
