import { useCallback, useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import {
  api,
  currentCustomerId,
  formatDate,
  formatMoney,
  toDateInputValue,
  fromDateInputValue,
  type Invoice,
  type Notification,
  type Policy,
  type Rental
} from '../api'

type Action = 'confirm' | 'cancel' | 'start' | 'complete'

export function RentalDetailPage() {
  const { id = '' } = useParams()
  const [rental, setRental] = useState<Rental | null>(null)
  const [notifications, setNotifications] = useState<Notification[]>([])
  const [policy, setPolicy] = useState<Policy | null>(null)
  const [invoice, setInvoice] = useState<Invoice | null>(null)
  const [newEnd, setNewEnd] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      const loaded = await api.getRental(id)
      setRental(loaded)
      setNewEnd(current => current || toDateInputValue(loaded.periodEnd))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Error desconocido')
    }
  }, [id])

  useEffect(() => {
    void load()
  }, [load])

  // Notificaciones, poliza y factura llegan por Kafka de forma asincrona: el
  // cliente consulta cada segundo hasta verlas. Es el comportamiento que las
  // pruebas E2E deben esperar, en vez de asumir inmediatez.
  useEffect(() => {
    const customerId = currentCustomerId()
    const poll = () => {
      api
        .listNotifications(customerId)
        .then(all => setNotifications(all.filter(n => n.rentalId === id)))
        .catch(() => undefined)
      api
        .listPolicies(id)
        .then(all => setPolicy(all[0] ?? null))
        .catch(() => undefined)
      api
        .listInvoices(id)
        .then(all => setInvoice(all[0] ?? null))
        .catch(() => undefined)
    }

    poll()
    const handle = setInterval(poll, 1000)
    return () => clearInterval(handle)
  }, [id])

  async function act(action: Action) {
    setBusy(true)
    setError(null)

    try {
      const updated =
        action === 'confirm'
          ? await api.confirmRental(id)
          : action === 'cancel'
            ? await api.cancelRental(id)
            : action === 'start'
              ? await api.startRental(id)
              : await api.completeRental(id)

      setRental(updated)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Error desconocido')
    } finally {
      setBusy(false)
    }
  }

  async function extend(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)

    try {
      setRental(await api.extendRental(id, fromDateInputValue(newEnd)))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Error desconocido')
    } finally {
      setBusy(false)
    }
  }

  if (!rental) {
    return <p data-testid="rental-loading">Cargando renta...</p>
  }

  const canExtend = ['Pending', 'Confirmed', 'Active'].includes(rental.status)

  return (
    <section>
      <h2>Reserva</h2>

      <dl className="rental-detail">
        <dt>Id</dt>
        <dd data-testid="rental-id">{rental.id}</dd>

        <dt>Estado</dt>
        <dd data-testid="rental-status">{rental.status}</dd>

        <dt>Periodo</dt>
        <dd data-testid="rental-period">
          {formatDate(rental.periodStart)} → {formatDate(rental.periodEnd)}
        </dd>

        <dt>Dias</dt>
        <dd data-testid="rental-days">{rental.totalDays}</dd>

        <dt>Tarifa diaria</dt>
        <dd data-testid="rental-daily-rate">{formatMoney(rental.dailyRate, rental.currency)}</dd>

        <dt>Total estimado</dt>
        <dd data-testid="rental-total">{formatMoney(rental.estimatedTotal, rental.currency)}</dd>

        {rental.finalTotal !== null && (
          <>
            <dt>Total final</dt>
            <dd data-testid="rental-final-total">{formatMoney(rental.finalTotal, rental.currency)}</dd>
          </>
        )}

        {rental.refundAmount !== null && (
          <>
            <dt>Reembolso</dt>
            <dd data-testid="rental-refund">{formatMoney(rental.refundAmount, rental.currency)}</dd>
          </>
        )}
      </dl>

      <div className="actions">
        <button data-testid="confirm-rental" onClick={() => act('confirm')} disabled={busy || rental.status !== 'Pending'}>
          Confirmar
        </button>
        <button data-testid="start-rental" onClick={() => act('start')} disabled={busy || rental.status !== 'Confirmed'}>
          Retirar vehiculo
        </button>
        <button data-testid="complete-rental" onClick={() => act('complete')} disabled={busy || rental.status !== 'Active'}>
          Devolver vehiculo
        </button>
        <button
          data-testid="cancel-rental"
          onClick={() => act('cancel')}
          disabled={busy || (rental.status !== 'Pending' && rental.status !== 'Confirmed')}
        >
          Cancelar
        </button>
      </div>

      {canExtend && (
        <form onSubmit={extend} className="inline-form" data-testid="extend-form">
          <label>
            Prorrogar hasta
            <input
              type="date"
              data-testid="extend-date"
              value={newEnd}
              onChange={event => setNewEnd(event.target.value)}
              required
            />
          </label>
          <button type="submit" data-testid="extend-rental" disabled={busy}>
            Prorrogar
          </button>
        </form>
      )}

      {error && (
        <p className="error" data-testid="rental-error">
          {error}
        </p>
      )}

      <h3>Poliza de seguro</h3>
      {policy ? (
        <dl className="rental-detail" data-testid="policy">
          <dt>Numero</dt>
          <dd data-testid="policy-number">{policy.number}</dd>
          <dt>Cobertura</dt>
          <dd data-testid="policy-coverage">{policy.coverage}</dd>
          <dt>Prima</dt>
          <dd data-testid="policy-premium">{formatMoney(policy.premium, policy.currency)}</dd>
          <dt>Estado</dt>
          <dd data-testid="policy-status">{policy.status}</dd>
          <dt>Vigencia</dt>
          <dd data-testid="policy-validity">
            {formatDate(policy.validFrom)} → {formatDate(policy.validTo)}
          </dd>
        </dl>
      ) : (
        <p data-testid="policy-empty">Aun no hay poliza para esta reserva.</p>
      )}

      <h3>Factura</h3>
      {invoice ? (
        <dl className="rental-detail" data-testid="invoice">
          <dt>Numero</dt>
          <dd data-testid="invoice-number">{invoice.number}</dd>
          <dt>Estado</dt>
          <dd data-testid="invoice-status">{invoice.status}</dd>
          <dt>Base imponible</dt>
          <dd data-testid="invoice-subtotal">{formatMoney(invoice.subtotal, invoice.currency)}</dd>
          <dt>Impuestos</dt>
          <dd data-testid="invoice-tax">{formatMoney(invoice.tax, invoice.currency)}</dd>
          <dt>Total</dt>
          <dd data-testid="invoice-total">{formatMoney(invoice.total, invoice.currency)}</dd>
        </dl>
      ) : (
        <p data-testid="invoice-empty">Aun no hay factura para esta reserva.</p>
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
        <p data-testid="notifications-empty">Aun no hay notificaciones para esta reserva.</p>
      )}
    </section>
  )
}
