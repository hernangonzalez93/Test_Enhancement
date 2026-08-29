# Estrategia de pruebas de TestEnforce

Este documento explica **qué** se prueba en cada nivel, **por qué** ahí y no en otro,
y **cómo** está implementado. El código de producción existe para dar soporte a las
pruebas: si algo del diseño parece más elaborado de lo necesario (puertos, reloj
inyectado, opciones configurables), casi siempre la razón está en este documento.

---

## 1. El mapa completo

```
                                    ┌────────────────────┐
                            HTTP    │   Pricing.Api      │  (sin estado)
                        ┌──────────►│   :5102            │
                        │           └────────────────────┘
┌──────────┐   HTTP  ┌──┴───────────┐   HTTP   ┌────────────────────┐
│ Frontend │────────►│ Rentals.Api  │─────────►│   Fleet.Api        │
│  React   │         │   :5101      │          │   :5103            │
│  :5173   │         └──────┬───────┘          └─────────┬──────────┘
└──────────┘                │                            ▲
                            │ publica                    │ consume
                            ▼                            │
                     ┌──────────────────────────────────┴─────┐
                     │        Kafka · topic rental-events      │
                     └──────────────────────────┬──────────────┘
                                                │ consume
                                                ▼
                                     ┌────────────────────┐
                                     │ Notifications.Api  │
                                     │   :5104            │
                                     └────────────────────┘

                     PostgreSQL :55432  (esquemas `rentals` y `fleet`)
```

`Rentals` es el servicio principal y el único con arquitectura hexagonal completa.
Los otros tres son deliberadamente pequeños: existen para que haya algo real al otro
lado de cada adaptador.

### Arquitectura hexagonal de Rentals

| Capa | Proyecto | Depende de | Contiene |
|---|---|---|---|
| Dominio | `Rentals.Domain` | **nada** | Agregado `Rental`, value objects, eventos de dominio, política de cancelación |
| Aplicación | `Rentals.Application` | Domain | Puertos (interfaces) y `RentalService`, que orquesta |
| Infraestructura | `Rentals.Infrastructure` | Application | Adaptadores de salida: EF Core, Kafka, HTTP |
| API | `Rentals.Api` | Infrastructure | Adaptador de entrada: Minimal API |

La regla que lo sostiene todo: **las dependencias apuntan hacia dentro**. El dominio
no sabe que existe PostgreSQL; la aplicación no sabe que existe Kafka. Eso es lo que
permite que el 60 % de las pruebas no necesiten Docker.

---

## 2. La pirámide en este repositorio

| # | Proyecto | Nivel | Métodos | Casos | Qué demuestra | Infra | Tiempo |
|---|---|---|---:|---:|---|---|---|
| 1 | `Rentals.Domain.Tests` | Unitaria pura | 66 | 96 | Las reglas de negocio | — | 2 s |
| 2 | `Rentals.Application.Tests` | Unitaria con dobles | 29 | 29 | La orquestación del caso de uso | — | 3 s |
| 3 | `Pricing.Api.Tests` | Unitaria + API | 18 | 27 | El motor de tarifas y su contrato HTTP | — | 3 s |
| 4 | `Notifications.Tests` | Unitaria + API | 14 | 14 | La traducción evento → notificación (Moq) | — | 3 s |
| 5 | `Rentals.Api.Tests` | API en memoria | 18 | 27 | El contrato HTTP y el mapeo de errores | — | 4 s |
| 6 | `Rentals.Infrastructure.Tests` | Adaptadores | 33 | 37 | SQL, mapeo EF, Kafka, HTTP, reintentos | Docker | 44 s |
| 7 | `Fleet.Api.Tests` | Servicio completo | 14 | 14 | API + PostgreSQL + regla de disponibilidad | Docker | 25 s |
| 8 | `Rentals.Integration.Tests` | Integración entre servicios | 12 | 12 | Que los tres servicios se entienden | Docker | 41 s |
| 9 | `Smoke.Tests` | Humo | 8 | 13 | Que el despliegue está vivo y cableado | compose | 2 s |
| 10 | `e2e/` (Playwright) | E2E | 12 | 12 | Recorridos de usuario reales | compose | 14 s |
| | **Total** | | **224** | **281** | | | |

«Métodos» son los `[Fact]` / `[Theory]` escritos; «casos» son las ejecuciones reales,
porque cada `[InlineData]` de un `[Theory]` cuenta como una prueba independiente en el
informe del runner.

Cada nivel dice también qué **no** demuestra, y eso importa tanto como lo que sí:

- El dominio prueba las reglas, no que estén conectadas a nada.
- La aplicación prueba la coordinación, no que los adaptadores funcionen.
- La API prueba el contrato HTTP, no que la base de datos guarde.
- Los adaptadores prueban SQL y mensajes, no el caso de uso completo.
- La integración prueba que los servicios se entienden, no la interfaz de usuario.
- El humo prueba que el despliegue vive, no las reglas de negocio.
- Las E2E prueban recorridos completos, no casos borde.

La forma de pirámide es intencionada: **muchas pruebas donde son baratas y precisas,
pocas donde son caras y frágiles**. Una regla de negocio (por ejemplo el recargo por
devolución tardía) se prueba una sola vez, en el dominio. No se vuelve a probar en la
API ni en E2E; ahí solo se comprueba que el resultado del dominio llega bien al
usuario.

---

## 3. Cómo ejecutar cada cosa

### Requisitos

- .NET 10 SDK
- Docker Desktop (para Testcontainers y para `docker compose`)
- Node 22 (frontend y Playwright)

### Detalle importante: el runner de pruebas

.NET 10 ya no admite ejecutar Microsoft.Testing.Platform a través de VSTest. Por eso
en la raíz hay un `global.json` con:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

Sin esa línea, `dotnet test` falla con
*«Testing with VSTest target is no longer supported…»*. Además, cada proyecto de
pruebas con xUnit v3 debe ser **ejecutable** (`OutputType=Exe`), cosa que
`Directory.Build.props` aplica automáticamente a todo lo que vive bajo `tests/`.

### Comandos

```bash
# Todo lo que no necesita Docker (rápido, ideal para el bucle de desarrollo)
dotnet test tests/Rentals.Domain.Tests
dotnet test tests/Rentals.Application.Tests
dotnet test tests/Rentals.Api.Tests
dotnet test tests/Pricing.Api.Tests
dotnet test tests/Notifications.Tests
```

```bash
# Pruebas que levantan contenedores con Testcontainers
dotnet test tests/Rentals.Infrastructure.Tests
dotnet test tests/Fleet.Api.Tests
dotnet test tests/Rentals.Integration.Tests
```

```bash
# Toda la suite .NET (Smoke necesita la pila arriba)
docker compose up -d --wait
dotnet test
```

```bash
# End to end en navegador
docker compose up -d --wait
cd e2e && npm install && npx playwright install chromium && npm test
```

---

## 4. Nivel 1 — Dominio: reglas puras, sin dependencias

`tests/Rentals.Domain.Tests`

`Rentals.Domain` no tiene ni una sola referencia de paquete. Esa restricción, que se
ve en su `.csproj` (vacío a propósito), es lo que hace que estas pruebas sean
instantáneas y deterministas.

### Qué se prueba

- **Value objects**: `Money`, `RentalPeriod`, `DriverLicense`. Invariantes,
  normalización, igualdad estructural.
- **La máquina de estados** del agregado `Rental`: cada transición legal **y cada
  transición ilegal**.
- **La política de cancelación**: una función pura con tramos, caso de manual para
  `[Theory]`.

### Patrón: una prueba nombra una regla, no un método

```csharp
[Fact]
public void Cancel_is_rejected_once_the_vehicle_was_picked_up()
{
    var rental = RentalBuilder.A().BuildActive();

    Should.Throw<InvalidRentalStateException>(() => rental.Cancel(Now.AddDays(11)));
}
```

El nombre dice la regla de negocio. Si el método `Cancel` se renombra, la prueba
sigue teniendo sentido; si la regla cambia, hay que reescribirla, que es exactamente
lo que se quiere.

### Patrón: `[Theory]` para fronteras

Los bugs viven en los límites. Por eso la política de cancelación se prueba con una
fila por frontera **exacta**:

```csharp
[Theory]
[InlineData(49, 100)]
[InlineData(48, 100)]   // límite exacto del reembolso total
[InlineData(47, 50)]
[InlineData(24, 50)]    // límite exacto del reembolso parcial
[InlineData(23, 25)]
public void RefundPercentageFor_applies_the_tier_matching_the_notice(int hoursAhead, decimal expected)
```

Escribir solo `50` y `72` habría dejado pasar un `>` en lugar de un `>=`.

### Patrón: Builder (Object Mother)

`TestSupport/RentalBuilder.cs`. Cada prueba declara **solo lo que la hace distinta**:

```csharp
var rental = RentalBuilder.A().WithDailyRate(50m).ForDays(3).BuildConfirmed();
```

Sin el builder, cada prueba necesitaría siete argumentos y el ruido escondería la
intención. Con él, `WithDailyRate(50m).ForDays(3)` es literalmente el enunciado del
caso.

### El reloj

`Rental.Request(..., DateTimeOffset now)` recibe el instante como parámetro. El
dominio **nunca** llama a `DateTimeOffset.UtcNow`. Si lo hiciera, una prueba sobre
«no se puede rentar en el pasado» dependería del momento de ejecución y fallaría de
forma intermitente. Todo el repositorio comparte un instante de referencia:
`FixedClock.DefaultNow`.

---

## 5. Nivel 2 — Aplicación: orquestación con dobles de prueba

`tests/Rentals.Application.Tests`

Aquí **no** se prueban reglas de negocio. Se prueba a quién se llama, con qué
argumentos, en qué orden, y qué se devuelve cuando un colaborador falla.

### El arnés

`RentalServiceHarness` crea el servicio con los seis puertos sustituidos por dobles
de NSubstitute y con un **camino feliz por defecto**:

```csharp
VehicleCatalog.FindAsync(Arg.Any<VehicleId>(), Arg.Any<CancellationToken>())
    .Returns(TestData.AvailableVehicle());
```

Gracias a eso, el `arrange` de cada prueba contiene únicamente la desviación que
esa prueba explora:

```csharp
_harness.VehicleCatalog
    .FindAsync(Arg.Any<VehicleId>(), Arg.Any<CancellationToken>())
    .Returns(TestData.UnavailableVehicle());
```

Quien lee la prueba ve de inmediato cuál es el escenario.

### Verificar el orden, no solo el resultado

```csharp
Received.InOrder(() =>
{
    _harness.Repository.AddAsync(Arg.Any<Rental>(), Arg.Any<CancellationToken>());
    _harness.UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
    _harness.EventPublisher.PublishAsync(...);
});
```

Publicar antes del commit permitiría notificar una renta que después no se guarda.
Esa decisión de diseño solo se puede blindar con una prueba de interacción; ninguna
prueba de estado la detectaría.

### Verificar lo que NO se llama

```csharp
result.Error.Code.ShouldBe("rental.overlapping");
await _harness.PricingCalculator.DidNotReceive()
    .QuoteAsync(Arg.Any<PricingRequest>(), Arg.Any<CancellationToken>());
```

Si hay solapamiento, no tiene sentido gastar una llamada de red a Pricing. Es una
prueba de eficiencia además de corrección.

### Errores esperados no son excepciones

`RentalService` devuelve `Result<RentalDto>`, no lanza. Un vehículo ocupado no es un
fallo del sistema: es una respuesta válida del caso de uso. Las excepciones de
dominio se capturan en la frontera y se traducen a `Result.Failure(código, mensaje)`.
Eso hace que la API tenga un único punto de traducción a HTTP.

### NSubstitute y Moq

El repositorio usa **las dos** a propósito, para poder compararlas:

| | NSubstitute (`Rentals.*`) | Moq (`Notifications.Tests`) |
|---|---|---|
| Crear | `Substitute.For<IPort>()` | `new Mock<IPort>()` |
| Configurar | `port.M(arg).Returns(x)` | `mock.Setup(p => p.M(arg)).Returns(x)` |
| Verificar | `await port.Received(1).M(arg)` | `mock.Verify(p => p.M(arg), Times.Once)` |
| Argumentos | `Arg.Any<T>()`, `Arg.Is<T>(...)` | `It.IsAny<T>()`, `It.Is<T>(...)` |
| Estricto | no hay modo estricto | `new Mock<T>(MockBehavior.Strict)` |

NSubstitute produce pruebas más cortas; Moq es más explícito y su `MockBehavior.Strict`
falla si se llama algo no configurado, lo que ayuda a detectar interacciones
accidentales. El patrón (arrange → act → verify) es idéntico.

---

## 6. Nivel 3 — Adaptadores: contra infraestructura real

`tests/Rentals.Infrastructure.Tests`

Un adaptador es, por definición, código que solo tiene sentido frente a la tecnología
que adapta. Probarlo con dobles es probar el doble.

### PostgreSQL real con Testcontainers

```csharp
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("rentals").WithUsername("rentals").WithPassword("rentals")
            .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();   // ← se prueban las migraciones reales
    }
}
```

El contenedor se comparte con `[CollectionDefinition]` + `ICollectionFixture<>`, de
modo que arranca una sola vez para toda la colección. El aislamiento entre pruebas se
consigue con un `TRUNCATE` en `InitializeAsync`, que es mucho más rápido que recrear
el contenedor.

**Por qué no un proveedor en memoria**: EF Core InMemory no ejecuta SQL, no valida
tipos de columna, no aplica migraciones y no detecta conflictos de concurrencia. Las
cinco cosas que estas pruebas necesitan comprobar.

Lo que se verifica aquí:

- Que los value objects sobreviven al viaje de ida y vuelta (`Money`, `RentalPeriod`,
  `DriverLicense` como *owned types*).
- Que las columnas opcionales (`final_total`, `refund_amount`) quedan en `NULL`.
- Que la consulta de solapamiento genera el SQL correcto, incluidos los casos
  contiguos y las rentas canceladas.
- Que dos escrituras concurrentes producen `DbUpdateConcurrencyException`.
- Que los `DateTimeOffset` vuelven en UTC.

### Concurrencia optimista sin ensuciar el dominio

La tabla tiene una columna `concurrency_stamp` que **no existe en el agregado**: es
una *shadow property* de EF Core, renovada en `SaveChangesAsync`:

```csharp
foreach (var entry in ChangeTracker.Entries<Rental>())
    if (entry.State is EntityState.Added or EntityState.Modified)
        entry.Property<Guid>(RentalConfiguration.ConcurrencyStamp).CurrentValue = Guid.CreateVersion7();
```

Así el dominio no carga con un contador de versión que es puro detalle de
persistencia, y aun así dos procesos que escriben a la vez chocan.

### Kafka real con Testcontainers

`KafkaEventPublisherTests` levanta un broker en modo KRaft y congela el **contrato de
transporte**, que es lo que comparten productor y consumidores:

- La clave de partición es el id de la renta (garantiza orden por renta).
- La cabecera `event-type` permite enrutar sin deserializar.
- El JSON publicado se deserializa de vuelta al mismo evento.

Un mock jamás detectaría que el productor escribe la cabecera con un nombre y el
consumidor la lee con otro.

### WireMock.Net para los clientes HTTP

`HttpAdapterTests` prueba la **traducción de protocolo**, incluidos los caminos que
el servicio real casi nunca produce y que en producción son justo los que rompen:

| Situación | Traducción esperada |
|---|---|
| Fleet responde 200 | `VehicleSnapshot` poblado |
| Fleet responde 404 | `null` — no existe **no es** un error |
| Fleet responde 500 | `ExternalServiceUnavailableException` |
| Fleet tarda más que el timeout | `ExternalServiceUnavailableException` |
| Pricing responde 400 | `ExternalServiceUnavailableException` |
| Pricing no escucha | `ExternalServiceUnavailableException` |

Esa distinción entre «no existe» y «no está disponible» es la que después convierte
la API en 404 o en 503.

### Reintentos y cableado de la inyección de dependencias

`ResilienceAndWiringTests` resuelve los puertos **desde el contenedor real**, con la
misma llamada `AddRentalsInfrastructure(configuration)` que usa la API. Usando
escenarios de WireMock comprueba que un 503 transitorio se reintenta y que la segunda
llamada tiene éxito:

```csharp
_server.Given(...).InScenario("pricing-retry").WillSetStateTo("recovered")
       .RespondWith(Response.Create().WithStatusCode(503));
_server.Given(...).InScenario("pricing-retry").WhenStateIs("recovered")
       .RespondWith(Response.Create().WithStatusCode(200).WithBody(...));

// ...
_server.LogEntries.Count().ShouldBe(2);   // un intento + un reintento
```

Los tiempos de reintento salen de configuración (`RetryDelayMilliseconds`) para que
la prueba tarde milisegundos en vez de segundos. **Hacer configurable lo que la
prueba necesita acelerar** es un principio que aparece varias veces en este
repositorio.

### Pruebas de contrato del mensaje

`EventContractTests` no necesita Docker: congela los nombres de los tipos de evento,
el nombre del topic y la forma del JSON. Si alguien renombra una propiedad, falla
aquí en un segundo, y no en producción tres semanas después.

---

## 7. Nivel 3.5 — API con `WebApplicationFactory`

`tests/Rentals.Api.Tests`

`WebApplicationFactory<T>` arranca la aplicación **completa en memoria**: enrutado,
serialización, middlewares, validación y mapeo de errores son reales. No hay puerto
TCP, no hay base de datos, no hay broker.

```csharp
public sealed class RentalsApiFactory : WebApplicationFactory<RentalsApiMarker>
{
    public IRentalService RentalService { get; } = Substitute.For<IRentalService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRentalService>();
            services.AddScoped(_ => RentalService);
        });
    }
}
```

Sustituir el **puerto de entrada** deja estas pruebas con una sola pregunta:
*dado un resultado del caso de uso, ¿qué devuelve HTTP?* Corren en milisegundos y
cubren cosas que solo existen en la capa web:

- 201 con cabecera `Location`.
- Validación de forma → 400 con `ValidationProblemDetails`.
- Cada código de error de negocio → su código HTTP (una `[Theory]` con diez filas).
- `application/problem+json` y la extensión `errorCode`.
- JSON malformado → 400; `text/plain` → 415.
- Ruta con un id que no es GUID → 404 por no coincidir la restricción de ruta.
- La cabecera `X-Correlation-Id` se propaga o se genera.

### El detalle del *marker*

Las *top-level statements* generan una clase `Program` en el **espacio de nombres
global**. Un proyecto de pruebas que referencie tres APIs a la vez tendría tres
clases `Program` ambiguas. Por eso cada servicio expone un ancla propia
(`RentalsApiMarker`, `FleetApiMarker`, …) dentro de su namespace, y las factorías la
usan como parámetro de tipo.

---

## 8. Nivel 4 — Integración entre servicios

`tests/Rentals.Integration.Tests`

Es el escenario más completo. En un solo proceso se levanta:

```
Rentals.Api   ── HTTP  ──►  Fleet.Api        (a través de su TestServer)
Rentals.Api   ── HTTP  ──►  WireMock          (Pricing simulado)
Rentals.Api   ── Kafka ──►  Fleet + Notifications (contenedor real)
todos         ── SQL   ──►  PostgreSQL        (contenedor real, dos esquemas)
```

### Por qué Pricing sí se simula

Su única lógica ya está cubierta por 20 pruebas unitarias del motor de tarifas. Lo
que aquí interesa medir es la **integración**, y además simularlo permite provocar a
voluntad el caso «Pricing caído», que es imposible de reproducir con el servicio real.

### El truco de conectar dos `WebApplicationFactory`

Rentals habla con Fleet por HTTP. En lugar de abrir un puerto, se le da al
`HttpClient` de Rentals el manejador del `TestServer` de Fleet:

```csharp
services
    .AddHttpClient<IVehicleCatalog, FleetHttpVehicleCatalog>(client =>
        client.BaseAddress = new Uri("http://fleet.local"))
    .ConfigurePrimaryHttpMessageHandler(() => fleetApp.Server.CreateHandler());
```

Hay serialización, enrutado, base de datos y errores HTTP reales, sin sockets.

### Esperar, no asumir

En un sistema por eventos la propagación es asíncrona. Afirmar de inmediato produce
pruebas intermitentes. Por eso el fixture ofrece:

```csharp
var updated = await IntegrationFixture.EventuallyAsync(async () =>
{
    await using var context = fixture.CreateFleetContext();
    var vehicle = await context.Vehicles.AsNoTracking().SingleAsync(v => v.Id == id);
    return !vehicle.Available;
});

updated.ShouldBeTrue("Fleet debía consumir rental.confirmed y bloquear el vehículo.");
```

Sondeo acotado con un mensaje que explica qué se esperaba. Nunca `Task.Delay(2000)`
a ciegas: eso es lento cuando funciona e insuficiente cuando falla.

### Qué demuestra este nivel y ningún otro

- Que la cadena de conexión del contenedor es válida y las migraciones se aplican.
- Que productor y consumidores usan el **mismo** topic y el mismo formato.
- Que dos grupos de consumidores distintos (Fleet y Notifications) reciben cada uno
  su copia del evento (*fan-out*).
- Que la regla de solapamiento funciona sobre SQL real, no sobre una lista en memoria.
- Que un 500 de Pricing sale de la API como 503 y no como 500.

---

## 9. Nivel 5 — Pruebas de humo

`tests/Smoke.Tests`

Se ejecutan **contra un despliegue ya levantado**, no contra código en memoria:

```bash
docker compose up -d --wait
dotnet test tests/Smoke.Tests
```

Responden a una única pregunta: *¿el despliegue está vivo y correctamente cableado?*
Por eso son pocas, rápidas y **no afirman reglas de negocio**.

- Los cuatro servicios responden `/health`.
- Los dos que tienen base de datos responden `/health/ready` — lo que demuestra que
  su cadena de conexión dentro del contenedor es correcta.
- La flota está sembrada y las migraciones de Rentals aplicadas.
- El frontend se sirve y su proxy alcanza los tres backends.
- Un recorrido mínimo crear → confirmar → notificación llega por Kafka.

### La distinción `/health` vs `/health/ready`

```csharp
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");   // PostgreSQL + consumidor de Kafka
```

`/health` responde en cuanto el proceso vive; `/health/ready` solo cuando puede
trabajar. En Rentals y Fleet eso incluye que PostgreSQL sea accesible; en Fleet y
Notifications, además, que su consumidor de Kafka tenga particiones asignadas.

Esa separación es la que hace útiles las pruebas de humo: distinguen «arrancó» de
«funciona». También es lo que usa `docker compose --wait`, y por eso la sonda tiene
que ser honesta: cuando solo miraba HTTP, las E2E arrancaban contra consumidores que
aún no consumían (ver los puntos 10 y 11 de la sección de errores reales).

### URLs configurables

Todas las direcciones salen de variables de entorno con valor por defecto local:

```csharp
private static readonly string RentalsUrl = Url("SMOKE_RENTALS_URL", "http://localhost:5101");
```

La misma suite sirve para validar un despliegue en staging después de publicar.

---

## 10. Nivel 6 — E2E con Playwright

`e2e/`

Navegador real, frontend, nginx, cuatro servicios, PostgreSQL y Kafka. Son las
pruebas más lentas y más frágiles del repositorio y por eso hay **doce**, no cien.
Solo recorridos de usuario completos que ninguna capa inferior puede demostrar.

### Selectores estables

Todo el frontend expone `data-testid`. Los tests usan `getByTestId`, nunca clases CSS
ni texto de maquetación:

```tsx
<dd data-testid="rental-status">{rental.status}</dd>
```

```ts
await expect(page.getByTestId('rental-status')).toHaveText('Confirmed')
```

Un cambio de diseño no rompe las pruebas; un cambio de comportamiento sí.

### Esperas correctas para eventos asíncronos

```ts
await page.getByTestId('confirm-rental').click()
await expect(page.getByTestId('notification-rental.confirmed')).toBeVisible()
```

La notificación viaja Rentals → Kafka → Notifications → sondeo del navegador.
`expect(...).toBeVisible()` de Playwright **reintenta automáticamente** hasta el
timeout. Esa es la forma correcta de esperar en un sistema por eventos; un
`waitForTimeout(3000)` sería lento y frágil a la vez.

Cuando hay que reintentar un bloque completo (navegar y comprobar), se usa `toPass`:

```ts
await expect(async () => {
  await page.goto('/')
  await expect(page.getByTestId(`vehicle-status-${vehicle.plate}`)).toHaveText('Rentado')
}).toPass({ timeout: 30_000 })
```

### Aislamiento: cada prueba crea su propio vehículo

Este es el punto más importante de la suite E2E, y se descubrió a base de fallos.

Confirmar una renta hace que Fleet marque el vehículo como **rentado**, y ese cambio
persiste en la base de datos. Si las pruebas se repartieran la flota sembrada, la
suite pasaría la primera vez y fallaría la segunda: el clásico fallo por estado
compartido.

La solución es crear el dato dentro de la prueba, usando la API de Fleet:

```ts
const vehicle = await createVehicle(request, { vehicleClass: 'luxury', dailyRate: 130 })
await createRental(page, vehicle.plate, period(2))
```

Ahora la suite es repetible **sin necesidad de limpiar la base entre ejecuciones**,
que es la propiedad que separa una suite E2E utilizable de una que todo el mundo
acaba ignorando.

### Ejecución en serie

```ts
fullyParallel: false,
workers: 1,
```

Estas pruebas comparten una pila real. Paralelizarlas sería mentirse: cualquier fallo
por concurrencia aparecería como intermitencia inexplicable. Con el aislamiento por
vehículo se podrían paralelizar, pero para una suite de doce pruebas que tarda quince
segundos no compensa la complejidad.

### Diagnóstico cuando falla

`playwright.config.ts` guarda traza, captura y vídeo solo en los fallos:

```ts
trace: 'retain-on-failure',
screenshot: 'only-on-failure',
video: 'retain-on-failure'
```

```bash
npx playwright show-trace test-results/<carpeta>/trace.zip
```

La traza incluye el DOM en cada paso, la línea de código y las peticiones de red.

---

## 11. Patrones transversales

### El reloj es un puerto

`IClock` existe únicamente por las pruebas, y es la dependencia que más dolor evita.
`FixedClock` permite escribir:

```csharp
_harness.Clock.SetTo(rental.Period.End.AddHours(30));
var result = await _harness.Service.CompleteAsync(rental.Id.Value);
result.Value.FinalTotal.ShouldBe(250m);   // dos días de recargo
```

Sin él, esa prueba sería imposible sin esperar 30 horas.

### Lo que la prueba necesita cambiar, va en configuración

| Opción | Para qué la usan las pruebas |
|---|---|
| `Kafka:Enabled` | Apagar el consumidor en pruebas de API que no necesitan broker |
| `Database:AutoMigrate` | Evitar que la API migre al arrancar bajo `WebApplicationFactory` |
| `Services:RetryDelayMilliseconds` | Que la prueba de reintentos tarde milisegundos |
| `Services:*BaseUrl` | Apuntar a WireMock |
| `SMOKE_*_URL` | Apuntar las pruebas de humo a otro entorno |

### Aislamiento por nivel

| Nivel | Cómo se aísla |
|---|---|
| Dominio / Aplicación | No hay estado: cada prueba construye sus objetos |
| Adaptadores | `TRUNCATE` antes de cada prueba; topic de Kafka único por prueba |
| Integración | `TRUNCATE` + resiembra; topic y grupos de consumo únicos por ejecución |
| E2E | Cada prueba crea su propio vehículo y su propio cliente (localStorage) |

### Nombres

Los métodos de prueba se leen como frases: `Cancel_from_pending_refunds_nothing_because_nothing_was_charged`.
Cuando falla la suite, el nombre debe bastar para saber qué regla se rompió, sin
abrir el código.

---

## 12. Errores reales que aparecieron construyendo esto

Se documentan porque son exactamente el tipo de fallo que estas pruebas existen para
atrapar, y porque varios son trampas fáciles de repetir.

**1. `Guid.CreateVersion7()` para nombres únicos.**
Los UUID v7 empiezan por una marca de tiempo, así que sus primeros caracteres
coinciden entre llamadas cercanas. Usarlos para generar sufijos de topic hacía que
todas las pruebas publicaran en el **mismo** topic y se pisaran. Para unicidad
aleatoria, `Guid.NewGuid()`.

**2. Consumir un topic que no existe lanza excepción.**
`consumer.Consume()` sobre un topic inexistente lanza `ConsumeException`, no devuelve
vacío. La prueba «publicar un lote vacío no produce nada» tuvo que reescribirse para
publicar primero un evento real y luego comprobar que el lote vacío no añadía un
segundo mensaje.

**3. Reconfigurar WireMock entre pruebas rompía conexiones vivas.**
`WireMockServer.Reset()` cerraba las conexiones que el `HttpClient` del servicio
mantenía en el pool, produciendo timeouts intermitentes sin relación con lo que se
probaba. La solución fue registrar **una sola** *mapping* con `WithCallback` y mover
el comportamiento variable a campos mutables del fixture.

**4. `localhost` dentro de un contenedor resuelve a `::1`.**
El *healthcheck* del frontend fallaba con «connection refused» mientras la página
respondía perfectamente desde el host: nginx solo escucha en IPv4. Se corrigió usando
`127.0.0.1` explícitamente.

**5. El `healthcheck` de Kafka apuntaba al listener equivocado.**
`localhost:29092` no existe: el listener `PLAINTEXT` está anunciado como
`kafka:29092`. El contenedor arrancaba bien y aun así se marcaba como *unhealthy*.

**6. `xmin` no se puede crear como columna.**
El primer mapeo de concurrencia optimista generaba una migración que intentaba
`CREATE` de la columna de sistema `xmin` de PostgreSQL. Se sustituyó por una *shadow
property* propia, que además deja el dominio limpio.

**7. `Shouldly` usa `IComparable` si el tipo lo implementa.**
`Money.ShouldNotBe(otraMoneda)` lanzaba `CurrencyMismatchException` porque
`CompareTo` rechaza comparar monedas distintas — que es la regla correcta. La prueba
se reescribió con `Equals` explícito, y se añadió una prueba que documenta que
comparar monedas distintas está prohibido.

**8. Un consumidor de Kafka que perdía su hilo bajo carga.**
Los `BackgroundService` de Fleet y Notifications arrancaban su bucle así:

```csharp
return Task.Factory.StartNew(
    () => ConsumeLoopAsync(stoppingToken),   // ← lambda async
    stoppingToken,
    TaskCreationOptions.LongRunning,
    TaskScheduler.Default);
```

`LongRunning` reserva un hilo dedicado, pero con un lambda **async** ese hilo solo
ejecuta hasta el primer `await`. A partir del primer mensaje, el `Consume()`
bloqueante pasaba a ejecutarse sobre hilos del **thread pool**. Cuando
`dotnet test` corre todos los proyectos en paralelo, el pool se satura, y como
inyecta hilos nuevos a razón de aproximadamente uno por segundo, el consumidor se
quedaba parado durante decenas de segundos.

El síntoma era desconcertante:
`Confirming_a_rental_makes_fleet_mark_the_vehicle_as_unavailable` fallaba de forma
intermitente **solo en la suite completa** y pasaba siempre en aislamiento. La pista
definitiva fue que el consumidor *de la propia prueba* (`ConsumeEventsFor`, que corre
en el hilo del test) sí recibía el evento, mientras que el consumidor *en segundo
plano* no: la única diferencia entre ambos era de dónde salía su hilo.

La corrección es que un consumidor de Kafka sea dueño de su hilo, con el bucle
completamente síncrono:

```csharp
_worker = new Thread(() => ConsumeLoop(stoppingToken))
{
    IsBackground = true,
    Name = "fleet-rental-events-consumer"
};
_worker.Start();
```

Dentro del bucle, `handler.HandleAsync(...).GetAwaiter().GetResult()` es seguro
porque el hilo es propio y no hay `SynchronizationContext`.

**9. Truncar una tabla que un consumidor está escribiendo.**
`ResetAsync` vaciaba `fleet.vehicles` antes de cada prueba de integración, mientras
el consumidor de Fleet seguía vivo procesando eventos de la prueba anterior. Si su
`SaveChangesAsync` caía después del `TRUNCATE`, EF veía cero filas afectadas y
lanzaba `DbUpdateConcurrencyException`; el consumidor la registraba y seguía, pero
con `EnableAutoCommit = true` el offset ya había avanzado y la actualización se
perdía. Ahora solo se vacía `rentals.rentals`, que nadie escribe en segundo plano, y
**cada prueba de integración crea su propio vehículo** a través de la API de Fleet —
el mismo criterio que ya seguía la suite E2E.

**10. `/health/ready` mentía en un servicio dirigido por eventos.**
Fleet y Notifications respondían «listo» en cuanto PostgreSQL era accesible, sin
decir nada sobre su consumidor de Kafka. `docker compose up -d --wait` daba la pila
por levantada y las E2E arrancaban contra consumidores que todavía se estaban uniendo
a su grupo: se midieron **13,4 s** entre publicar un evento y procesarlo. Ahora ambos
servicios exponen un `IHealthCheck` que solo pasa cuando el consumidor ha recibido
particiones:

```csharp
.SetPartitionsAssignedHandler((_, partitions) =>
{
    readiness.MarkReady();
    ...
})
```

y el `healthcheck` de compose apunta a `/health/ready`. Una sonda de readiness debe
significar «puede trabajar», y en un sistema por eventos eso incluye estar
consumiendo.

**11. El topic no existía hasta que alguien publicaba.**
La corrección anterior destapó un problema de fondo: en un clúster recién arrancado
nadie ha publicado todavía, el topic no existe, y el consumidor queda suscrito a la
nada sin recibir particiones nunca. Antes esto pasaba inadvertido porque la sonda no
lo miraba. Depender de `auto.create.topics.enable` no es una opción seria: en
clústeres reales suele estar desactivado. Ahora cada consumidor crea su topic de
forma idempotente antes de suscribirse:

```csharp
admin.CreateTopicsAsync([new TopicSpecification { Name = _options.Topic, ... }])
     .GetAwaiter().GetResult();
// ...
catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
```

Un arranque en frío pasó de **fallar a los 2 m 37 s** a estar sano en **45 s**, con
las E2E en verde ejecutadas inmediatamente después.

**12. nginx resuelve los upstreams una sola vez.**
Tras reconstruir `fleet-api` y `notifications-api`, el proxy devolvía **404 con
cuerpo vacío** mientras los servicios respondían 200 directamente. nginx resuelve los
nombres al cargar la configuración; al recrearse los contenedores cambian de IP, y
como Docker las recicla, las peticiones acababan en otro contenedor —de ahí el 404 en
lugar del 502 que uno esperaría. La corrección es un `resolver` apuntando al DNS
embebido de Docker más una variable en `proxy_pass`, que fuerza a resolver en cada
petición:

```nginx
resolver 127.0.0.11 valid=10s ipv6=off;

location /api/vehicles {
    set $upstream_fleet fleet-api;
    proxy_pass http://$upstream_fleet:8080$request_uri;
}
```

**13. Las fechas aleatorias de las E2E se salían de la vigencia de la licencia.**
La suite fallaba con `rental.license_expired`, un error real del dominio pero ajeno a
lo que la prueba quería demostrar. Los datos generados deben mantenerse dentro del
rango válido de **todas** las reglas, no solo de la que se está probando.

---

## 13. Dónde poner una prueba nueva

| Si quieres comprobar… | Va en… |
|---|---|
| Una regla de negocio, un cálculo, una transición | `Rentals.Domain.Tests` |
| Que el caso de uso llama a los puertos correctos | `Rentals.Application.Tests` |
| Un código de estado HTTP o la forma del JSON | `Rentals.Api.Tests` |
| Una consulta SQL, un mapeo EF, un mensaje de Kafka | `Rentals.Infrastructure.Tests` |
| Que dos servicios se entienden de verdad | `Rentals.Integration.Tests` |
| Que el despliegue está vivo | `Smoke.Tests` |
| Un recorrido completo del usuario | `e2e/` |

Regla práctica: **baja todo lo que puedas**. Si una prueba se puede escribir en el
dominio, no la escribas en integración. Cada nivel que subes multiplica el tiempo de
ejecución y la probabilidad de intermitencia.

Y la regla complementaria: **no dupliques**. El recargo por devolución tardía se
prueba una vez, con `[Theory]`, en el dominio. En E2E solo se comprueba que el número
que calculó el dominio aparece en pantalla.
