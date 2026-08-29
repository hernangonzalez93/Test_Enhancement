import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// En desarrollo Vite hace de proxy hacia cada servicio, igual que hace nginx
// en el contenedor. Asi el codigo del cliente usa siempre rutas relativas y las
// pruebas E2E funcionan contra `npm run dev` o contra docker compose sin cambios.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api/rentals': 'http://localhost:5101',
      '/api/vehicles': 'http://localhost:5103',
      '/api/notifications': 'http://localhost:5104',
      '/api/quotes': 'http://localhost:5102',
      '/api/insurance': 'http://localhost:5106',
      '/api/policies': 'http://localhost:5106',
      '/api/invoices': 'http://localhost:5107',
      '/api/pricing': 'http://localhost:5102'
    }
  }
})
