# TestEnforce

Sistema de **renta de vehículos** en .NET 10 con arquitectura hexagonal, comunicación
por eventos sobre Kafka, PostgreSQL, frontend en React y todo en contenedores.

El objetivo del repositorio no es el dominio: es el **stack de pruebas**. Hay 411
pruebas repartidas en seis niveles, desde reglas de negocio puras hasta recorridos de
usuario en un navegador real.

> **Lee [`docs/TESTING.md`](docs/TESTING.md).** Es el documento principal: explica qué
> se prueba en cada nivel, por qué ahí, cómo está implementado y qué errores reales
> aparecieron al construirlo.
>
> Y [`docs/KAFKA.md`](docs/KAFKA.md) para entender la mensajería: cómo viaja un
> evento, qué significan las piezas (topic, clave, offset, grupo de consumo) y cómo
> observarlo todo mientras corre.
>
> Y [`docs/CONFIGURACION.md`](docs/CONFIGURACION.md) para cómo se resuelve un valor de
> configuración: el orden de las fuentes, la traducción de `__` a `:` y por qué la
> misma imagen sirve para local y para producción.

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
| Kafka UI | http://localhost:5105 |
| Insurances API | http://localhost:5106 |
| Billing API | http://localhost:5107 |
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
  Billing/
    Billing.Domain/               Agregado Invoice. Cero dependencias.
    Billing.Application/          Puertos + InvoiceService
    Billing.Infrastructure/       EF Core (OwnsMany para las líneas)
    Billing.Api/                  Controllers clásicos + consumidor Kafka
  Pricing/Pricing.Api/            Cálculo de tarifas (HTTP, sin estado)
  Fleet/Fleet.Api/                Inventario de vehículos (HTTP + consumidor Kafka)
  Notifications/Notifications.Api/ Consumidor Kafka + API de consulta
  Insurances/Insurances.Api/      Primas y pólizas (HTTP + consumidor Kafka)

tests/
  TestSupport/                    Reloj fijo, builders y datos compartidos
  Rentals.Domain.Tests/           Unitarias puras
  Rentals.Application.Tests/      Unitarias con NSubstitute
  Rentals.Api.Tests/              WebApplicationFactory
  Rentals.Infrastructure.Tests/   Testcontainers (PostgreSQL, Kafka) + WireMock
  Rentals.Integration.Tests/      Los tres servicios juntos, infraestructura real
  Billing.Domain.Tests/           Reglas de la factura
  Billing.Application.Tests/      Orquestación con NSubstitute
  Billing.Api.Tests/              Controllers clásicos
  Billing.Infrastructure.Tests/   EF Core contra PostgreSQL real
  Pricing.Api.Tests/              Motor de tarifas + contrato HTTP
  Insurances.Api.Tests/           Primas y ciclo de vida de pólizas
  Fleet.Api.Tests/                API + PostgreSQL real
  Notifications.Tests/            Unitarias con Moq
  Smoke.Tests/                    Contra el despliegue de docker compose

frontend/                         React 19 + Vite, servido por nginx
e2e/                              Playwright
docs/TESTING.md                   Documento principal de la estrategia de pruebas
docs/KAFKA.md                     Cómo funciona y cómo observar la mensajería
docs/CONFIGURACION.md             Proveedores de configuración, prioridades y secretos
```

---

## Decisiones de diseño

**Sin CQRS ni mediator.** `RentalService` es una clase que recibe sus puertos por
constructor y expone métodos. La indirección de un mediador no aportaría nada aquí y
haría las pruebas menos directas.

**Eventos, no llamadas.** Rentals publica en el topic `rental-events`. Fleet bloquea o
libera vehículos, Notifications genera avisos, Insurances emite y gestiona pólizas, y
Billing factura al completar o al cancelar. Ninguno conoce a Rentals: solo el contrato
de `Shared.Contracts`.

**Dos estilos de adaptador de entrada sobre la misma arquitectura.** Rentals y Billing
son ambos hexagonales, pero Rentals usa Minimal API y Billing usa **controllers
clásicos**. Sirve para comparar los dos estilos con el resto de condiciones iguales.

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
- Estados de la renta: `Pending → Confirmed → Active → Completed`, con salida a
  `Cancelled` únicamente desde `Pending` y `Confirmed`.
- Cancelar **antes de confirmar** no reembolsa ni cobra nada: nunca hubo cargo. Tras
  confirmar, lo que no se reembolsa se cobra, y reembolso más penalización suman
  siempre el total.
- Prima del seguro: el mayor entre un mínimo diario y un porcentaje del importe de la
  renta, según la cobertura (basic, standard, premium).
- Póliza: `Draft → Active → Expired`, con salida a `Cancelled` desde Draft y Active.
  Prorrogar la renta alarga la vigencia y recalcula la prima.
- Factura: se emite al completar (total final) o al cancelar (solo la penalización).
  IVA del 19 %. Estados `Draft → Issued → Paid`, con `Void` desde Draft e Issued; una
  factura pagada es inmutable. El pago debe cuadrar exactamente con el total.

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
- **Kafka UI** (http://localhost:5105) permite inspeccionar el topic `rental-events`:
  mensajes con su clave, cabeceras y payload, y el estado de los dos grupos de
  consumo (`fleet-service` y `notifications-service`) con su *lag*. Es una
  herramienta de diagnóstico, no parte del sistema: `docker compose stop kafka-ui`
  y todo lo demás sigue igual. No declara `healthcheck` a propósito, para no añadir
  sus ~30 s de arranque a cada `up --wait`; si abres la página justo después de
  levantar la pila, puede tardar un poco en responder.
- Todos los servicios llevan `restart: unless-stopped`, así que la pila vuelve sola
  tras reiniciar Docker Desktop o la máquina. Sin esa política los contenedores salen
  con código 255 y hay que levantarlos a mano.
- Para reiniciar los datos: `docker compose down -v && docker compose up -d --wait`.
