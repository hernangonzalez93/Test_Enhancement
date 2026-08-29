# TestEnforce

Sistema de **renta de vehículos** en .NET 10 con arquitectura hexagonal, comunicación
por eventos sobre Kafka, PostgreSQL, frontend en React y todo en contenedores.

El objetivo del repositorio no es el dominio: es el **stack de pruebas**. Hay 281
pruebas repartidas en seis niveles, desde reglas de negocio puras hasta recorridos de
usuario en un navegador real.

> **Lee [`docs/TESTING.md`](docs/TESTING.md).** Es el documento principal: explica qué
> se prueba en cada nivel, por qué ahí, cómo está implementado y qué errores reales
> aparecieron al construirlo.

---

## Arranque rápido

```bash
docker compose up -d --wait
```

| Servicio | URL |
|---|---|
| Frontend | http://localhost:5173 |
| Rentals API | http://localhost:5101 |
| Pricing API | http://localhost:5102 |
| Fleet API | http://localhost:5103 |
| Notifications API | http://localhost:5104 |
| PostgreSQL | `localhost:55432` · usuario/clave `testenforce` |
| Kafka | `localhost:59092` |

> PostgreSQL y Kafka se publican en puertos altos (55432 y 59092) para no chocar con
> otras pilas locales que ya usen 5432 y 9092.

Cada API expone su OpenAPI en `/openapi/v1.json`.

---

## Ejecutar las pruebas

```bash
# Rápidas, sin Docker
dotnet test tests/Rentals.Domain.Tests
dotnet test tests/Rentals.Application.Tests
dotnet test tests/Rentals.Api.Tests
```

```bash
# Con Testcontainers (necesitan Docker, no compose)
dotnet test tests/Rentals.Infrastructure.Tests
dotnet test tests/Fleet.Api.Tests
dotnet test tests/Rentals.Integration.Tests
```

```bash
# Toda la suite .NET — Smoke.Tests necesita la pila levantada
docker compose up -d --wait
dotnet test
```

```bash
# End to end en navegador
cd e2e
npm install
npx playwright install chromium
npm test
```

---

## Estructura

```
src/
  Shared/Shared.Contracts/        Eventos de integración (contrato entre servicios)
  Rentals/
    Rentals.Domain/               Reglas de negocio. Cero dependencias.
    Rentals.Application/          Puertos + RentalService (orquestación)
    Rentals.Infrastructure/       Adaptadores: EF Core, Kafka, HTTP
    Rentals.Api/                  Minimal API
  Pricing/Pricing.Api/            Cálculo de tarifas (HTTP, sin estado)
  Fleet/Fleet.Api/                Inventario de vehículos (HTTP + consumidor Kafka)
  Notifications/Notifications.Api/ Consumidor Kafka + API de consulta

tests/
  TestSupport/                    Reloj fijo, builders y datos compartidos
  Rentals.Domain.Tests/           Unitarias puras
  Rentals.Application.Tests/      Unitarias con NSubstitute
  Rentals.Api.Tests/              WebApplicationFactory
  Rentals.Infrastructure.Tests/   Testcontainers (PostgreSQL, Kafka) + WireMock
  Rentals.Integration.Tests/      Los tres servicios juntos, infraestructura real
  Pricing.Api.Tests/              Motor de tarifas + contrato HTTP
  Fleet.Api.Tests/                API + PostgreSQL real
  Notifications.Tests/            Unitarias con Moq
  Smoke.Tests/                    Contra el despliegue de docker compose

frontend/                         React 19 + Vite, servido por nginx
e2e/                              Playwright
docs/TESTING.md                   Documento principal de la estrategia de pruebas
```

---

## Decisiones de diseño

**Sin CQRS ni mediator.** `RentalService` es una clase que recibe sus puertos por
constructor y expone métodos. La indirección de un mediador no aportaría nada aquí y
haría las pruebas menos directas.

**Eventos, no llamadas.** Rentals publica en el topic `rental-events`. Fleet consume
para bloquear o liberar vehículos; Notifications consume para generar avisos. Ninguno
de los dos conoce a Rentals: solo el contrato de `Shared.Contracts`.

**Dos consultas síncronas.** Rentals llama a Fleet (¿existe y está disponible este
vehículo?) y a Pricing (¿cuánto cuesta?) por HTTP, porque necesita la respuesta para
decidir. Todo lo demás viaja por eventos.

**El dominio no conoce nada.** `Rentals.Domain` no tiene ni una referencia de
paquete. Ni EF Core, ni JSON, ni fechas del sistema: el instante actual entra siempre
como parámetro.

**Resultados en vez de excepciones para errores esperados.** Un vehículo ocupado
devuelve `Result.Failure("rental.overlapping", …)`, que la API traduce a 409 en un
único punto (`ErrorMapping`).

---

## Reglas de negocio implementadas

- Periodo mínimo de un día y máximo de 90; cada fracción de 24 h se factura como día
  completo.
- No se puede rentar con fecha de inicio en el pasado.
- La licencia debe seguir vigente el día de la devolución.
- Un vehículo no puede tener dos rentas vivas que se solapen (intervalos semiabiertos:
  contiguas sí se permiten).
- Tarifa = tarifa base × multiplicador de clase + extras por día, con descuento del
  10 % a partir de 7 días y del 20 % a partir de 30.
- Reembolso al cancelar: 100 % con más de 48 h, 50 % entre 24 y 48 h, 25 % entre 2 y
  24 h, 0 % después. Cancelar antes de confirmar no reembolsa nada porque no se cobró.
- Devolución tardía: cada bloque de 24 h iniciado se cobra a tarifa plena.
- Estados: `Pending → Confirmed → Active → Completed`, con salida a `Cancelled`
  únicamente desde `Pending` y `Confirmed`.

---

## Notas operativas

- Las migraciones se aplican al arrancar el contenedor (`Database__AutoMigrate=true`).
  Bajo `WebApplicationFactory` está desactivado a propósito.
- `/health/ready` de Fleet y Notifications solo pasa cuando su consumidor de Kafka ha
  recibido particiones, así que `docker compose up -d --wait` espera a que el sistema
  esté realmente consumiendo antes de devolver el control.
- Cada consumidor crea el topic `rental-events` de forma idempotente al arrancar, sin
  depender de `auto.create.topics.enable`.
- Fleet siembra ocho vehículos con identificadores fijos, para que las pruebas de humo
  y E2E puedan referenciarlos.
- Para reiniciar los datos: `docker compose down -v && docker compose up -d --wait`.
