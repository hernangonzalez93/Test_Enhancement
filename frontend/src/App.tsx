import { Link, Route, Routes } from 'react-router-dom'
import { VehiclesPage } from './pages/VehiclesPage'
import { NewRentalPage } from './pages/NewRentalPage'
import { RentalDetailPage } from './pages/RentalDetailPage'
import { currentCustomerId } from './api'

export function App() {
  return (
    <div className="layout">
      <header>
        <h1>TestEnforce · Renta de vehiculos</h1>
        <nav>
          <Link data-testid="nav-vehicles" to="/">
            Flota
          </Link>
          <Link data-testid="nav-new-rental" to="/new">
            Nueva renta
          </Link>
        </nav>
        <p className="customer">
          Cliente: <span data-testid="customer-id">{currentCustomerId()}</span>
        </p>
      </header>

      <main>
        <Routes>
          <Route path="/" element={<VehiclesPage />} />
          <Route path="/new" element={<NewRentalPage />} />
          <Route path="/rentals/:id" element={<RentalDetailPage />} />
        </Routes>
      </main>
    </div>
  )
}
