import { defineConfig, devices } from '@playwright/test'

/**
 * Las pruebas E2E se ejecutan contra la pila levantada con docker compose.
 *
 *   docker compose up -d --wait
 *   cd e2e && npm install && npm run install-browsers && npm test
 *
 * BASE_URL permite apuntar a otro entorno (por ejemplo `npm run dev` de Vite
 * en http://localhost:5173, o un entorno de staging).
 */
export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 15_000 },

  // En serie: estas pruebas comparten el estado de la flota, igual que los
  // usuarios reales comparten los vehiculos. Paralelizarlas seria mentirse.
  fullyParallel: false,
  workers: 1,

  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],

  use: {
    baseURL: process.env.BASE_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure'
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] }
    }
  ]
})
