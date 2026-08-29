# Estrategia de pruebas de TestEnforce

Este documento explica **qué** se prueba en cada nivel, **por qué** ahí y no en otro,
y **cómo** está implementado. El código de producción existe para dar soporte a las
pruebas: si algo del diseño parece más elaborado de lo necesario (puertos, reloj
inyectado, opciones configurables), casi siempre la razón está en este documento.

---

## 1. El mapa completo

```
                          HTTP    ┌────────────────────┐
                       ┌─────────►│   Pricing.Api      │  (sin estado)
                       │          │   :5102            │
┌──────────┐        ┌──┴────────┐ └────────────────────┘
│ Frontend │  HTTP  │ Rentals   │        HTTP   ┌────────────────────┐
│  React   │───────►│ Api :5101 │──────────────►│   Fleet.Api :5103  │
│  :5173   │        └─────┬─────┘               └─────────┬──────────┘
└──────────┘              │ publica                       ▲ consume
                          ▼                               │
        ┌─────────────────────────────────────────────────┴──────┐
        │              Kafka · topic rental-events                │
        └──┬──────────────────┬───────────────────┬───────────────┘
           │ consume          │ consume           │ consume
           ▼                  ▼                   ▼
  ┌─────────────────┐ ┌────────────────┐ ┌──────────────────┐
  │ Notifications   │ │ Insurances.Api │ │  Billing.Api     │
  │ Api :5104       │ │ :5106          │ │  :5107           │
  └─────────────────┘ └────────────────┘ └──────────────────┘

  PostgreSQL :55432  (esquemas rentals, fleet y billing)
  Kafka UI   :5105   (diagnostico)
```

**Dos servicios son hexagonales y cuatro son ligeros**, y ese contraste es
deliberado:

| Servicio | Arquitectura | Adaptador de entrada |
|---|---|---|
| `Rentals` | Hexagonal (4 proyectos) | Minimal API |
| `Billing` | Hexagonal (4 proyectos) | **Controllers clásicos** |
| `Pricing`, `Fleet`, `Notifications`, `Insurances` | Un solo proyecto | Minimal API |

Rentals y Billing muestran la misma arquitectura con dos estilos de adaptador de
entrada distintos. Los cuatro ligeros existen para que haya algo real al otro lado de
cada adaptador, sin pagar la ceremonia de cuatro capas donde no aporta.

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
| 1 | `Rentals.Domain.Tests` | Unitaria pura | 68 | 101 | Las reglas de negocio de la renta | — | 2 s |
| 2 | `Billing.Domain.Tests` | Unitaria pura | 26 | 30 | Las reglas de la factura | — | 2 s |
| 3 | `Rentals.Application.Tests` | Unitaria con dobles | 31 | 31 | La orquestación del caso de uso (NSubstitute) | — | 3 s |
| 4 | `Billing.Application.Tests` | Unitaria con dobles | 11 | 11 | Idempotencia y traducción de errores | — | 2 s |
| 5 | `Pricing.Api.Tests` | Unitaria + API | 18 | 27 | El motor de tarifas y su contrato HTTP | — | 3 s |
| 6 | `Insurances.Api.Tests` | Unitaria + API | 35 | 40 | Primas y ciclo de vida de las pólizas | — | 3 s |
| 7 | `Notifications.Tests` | Unitaria + API | 18 | 18 | Evento → notificación, con Moq | — | 3 s |
| 8 | `Rentals.Api.Tests` | API en memoria | 20 | 29 | Contrato HTTP en Minimal API | — | 4 s |
| 9 | `Billing.Api.Tests` | API en memoria | 12 | 16 | Contrato HTTP en controllers clásicos | — | 4 s |
| 10 | `Rentals.Infrastructure.Tests` | Adaptadores | 34 | 38 | SQL, mapeo EF, Kafka, HTTP, reintentos | Docker | 40 s |
| 11 | `Billing.Infrastructure.Tests` | Adaptadores | 7 | 7 | `OwnsMany`, índice único, totales calculados | Docker | 20 s |
| 12 | `Fleet.Api.Tests` | Servicio completo | 14 | 14 | API + PostgreSQL + disponibilidad | Docker | 25 s |
| 13 | `Rentals.Integration.Tests` | Integración entre servicios | 12 | 12 | Que los servicios se entienden | Docker | 41 s |
| 14 | `Smoke.Tests` | Humo | 10 | 19 | Que el despliegue está vivo y cableado | compose | 2 s |
| 15 | `e2e/` (Playwright) | E2E | 18 | 18 | Recorridos de usuario reales | compose | 55 s |
| | **Total** | | **334** | **411** | | | |

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

### Qué necesita cada prueba

Conviene aclarar un malentendido habitual: **ninguna prueba corre *dentro* de un
contenedor**. Todas se ejecutan en la máquina de desarrollo (`dotnet test`,
`npm test`). Lo que cambia es su relación con la infraestructura, y hay tres grupos.

**Grupo 1 — no necesitan nada (273 de 411 casos, el 66 %)**

| Proyecto | Casos |
|---|---:|
| `Rentals.Domain.Tests` | 101 |
| `Insurances.Api.Tests` | 40 |
| `Rentals.Application.Tests` | 31 |
| `Billing.Domain.Tests` | 30 |
| `Rentals.Api.Tests` | 29 |
| `Pricing.Api.Tests` | 27 |
| `Notifications.Tests` | 18 |
| `Billing.Api.Tests` | 16 |
| `Billing.Application.Tests` | 11 |

Con Docker apagado corren igual, en unos 6 segundos en total. No es casualidad: es el
retorno directo de la arquitectura hexagonal. Los dominios no tienen dependencias y
las capas de aplicación solo conocen interfaces, así que dos tercios de la suite no
tocan infraestructura.

**Grupo 2 — levantan sus propios contenedores (71 casos)**

Mediante Testcontainers, efímeros y creados por la propia prueba. Necesitan **Docker
corriendo, pero no `docker compose`**.

| Proyecto | Contenedores | Casos |
|---|---|---:|
| `Rentals.Infrastructure.Tests` | PostgreSQL + Kafka | 38 |
| `Fleet.Api.Tests` | PostgreSQL | 14 |
| `Rentals.Integration.Tests` | PostgreSQL + Kafka | 12 |
| `Billing.Infrastructure.Tests` | PostgreSQL | 7 |

Se comparten por colección con `ICollectionFixture<>`, así que arrancan una vez por
proyecto y no una vez por prueba. Un auxiliar `testcontainers/ryuk` los limpia al
terminar aunque el proceso muera.

**Grupo 3 — necesitan la pila ya desplegada (37 casos)**

| Proyecto | Requisito |
|---|---|
| `Smoke.Tests` (19) | `docker compose up -d --wait` |
| `e2e/` (18) | compose + Chromium |

Estos no crean nada: verifican un despliegue que ya existe, con sus diez contenedores.

#### Dos cosas que parecen contenedores y no lo son

**WireMock.Net** es un servidor HTTP **en proceso**, no un contenedor. Arranca en
milisegundos sobre un puerto local aleatorio. Aparece en `Rentals.Infrastructure.Tests`
y en `Rentals.Integration.Tests`, pero no suma ni un contenedor.

**`WebApplicationFactory`** levanta la API **en memoria**, sin abrir un puerto TCP.
Por eso `Pricing.Api.Tests`, `Notifications.Tests` y `Rentals.Api.Tests` prueban la
API completa —enrutado, serialización, middlewares, validación— sin Docker.

`Fleet.Api.Tests` usa las dos cosas a la vez: la API va en memoria (gratis) y solo
PostgreSQL es un contenedor.

#### Varios Kafka en la misma máquina, y cuál estás mirando

Ejecutar este repositorio puede dejar **tres brokers de Kafka distintos** funcionando a
la vez en el mismo equipo. Confundirlos es una fuente de desconcierto real, así que
conviene tenerlos claros:

| Broker | Puerto en el host | Vida | Quién lo ve |
|---|---|---|---|
| El de `docker compose` de este proyecto | **59092** | Mientras la pila esté levantada | Kafka UI en :5105, humo, E2E |
| Los efímeros de **Testcontainers** | Puerto aleatorio | Solo durante una ejecución de pruebas | Únicamente el código de la prueba |
| Cualquier otra pila local tuya | Normalmente 9092 | Ajena a este repositorio | Sus propias herramientas |

Por eso `docker-compose.yml` publica PostgreSQL en **55432** y Kafka en **59092** en vez
de los puertos estándar: para **convivir** con otras pilas que ya usen 5432 y 9092, en
lugar de pelearse con ellas por el puerto. Lo mismo con Kafka UI, que va al 5105 y no
al 8080.

Las dos consecuencias prácticas:

- **Kafka UI no ve los brokers de Testcontainers.** Sirve para depurar el sistema
  desplegado, las pruebas de humo y las E2E; no `Rentals.Infrastructure.Tests` ni
  `Rentals.Integration.Tests`, que inspeccionan los mensajes desde el propio código de
  la prueba con un consumidor creado al efecto.
- **Si abres una UI de Kafka y no ves lo que esperas, comprueba primero a qué broker
  está apuntando.** Un topic `rental-events` vacío suele significar que estás mirando
  el clúster equivocado, no que la publicación haya fallado.

Las pruebas de integración van un paso más allá: además de un broker propio, usan un
**topic con nombre único por ejecución** y **grupos de consumo únicos**, de modo que dos
ejecuciones simultáneas no se pisan ni siquiera dentro del mismo contenedor.

#### Un detalle práctico

Si se ejecuta `dotnet test` **sin** la pila levantada, los 273 casos pasan salvo los
13 de `Smoke.Tests`, que fallan por conexión rechazada. Es el comportamiento
esperado, no un fallo real: esas pruebas existen precisamente para comprobar un
despliegue.

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

`dotnet test` acepta **un solo** proyecto por invocación (posicional o con
`--project`). Pasar varias rutas seguidas no da error visible: ejecuta la primera y
se detiene, así que conviene una línea por proyecto.

```bash
# Grupo 1 · bucle de desarrollo · ~3 s en total · Docker puede estar apagado
dotnet test tests/Rentals.Domain.Tests
dotnet test tests/Rentals.Application.Tests
dotnet test tests/Rentals.Api.Tests
dotnet test tests/Pricing.Api.Tests
dotnet test tests/Notifications.Tests
```

```bash
# Grupo 2 · antes de commit · ~110 s · necesita Docker, NO compose
dotnet test tests/Rentals.Infrastructure.Tests
dotnet test tests/Fleet.Api.Tests
dotnet test tests/Rentals.Integration.Tests
```

```bash
# Toda la suite .NET · Smoke.Tests necesita la pila levantada
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

Esa frase suele generar dudas, así que conviene desarrollarla.

### La idea: el dominio prueba decisiones, la aplicación prueba la coreografía

`RentalService` no decide **nada** de negocio. Si se leen las líneas de
`RequestAsync` separándolas en dos columnas, queda claro dónde vive cada cosa:

| Coreografía — responsabilidad de Application | Decisión — responsabilidad de Domain |
|---|---|
| Preguntar a Fleet si el vehículo existe | Si el periodo es válido |
| Consultar el solapamiento en el repositorio | Si la licencia cubre la renta |
| Pedir la tarifa a Pricing | Cuántos días facturables hay |
| Guardar y confirmar la transacción | Cuánto cuesta la renta |
| Publicar los eventos de integración | Qué transiciones de estado son legales |

Todo lo de la columna derecha está delegado a `Rental.Request(...)` y a los value
objects. Lo de la izquierda **es** el trabajo de `RentalService`, y tiene errores
propios que ninguna prueba de dominio puede detectar: el dominio ni siquiera sabe que
Fleet, Kafka o PostgreSQL existen.

### Lo que se sustituye y lo que no

En esta capa se sustituyen **los puertos, no el dominio**. `Rental`, `Money` y
`RentalPeriod` son objetos reales en estas pruebas; solo se falsea la entrada/salida:
base de datos, HTTP, bus de eventos y reloj.

Es una distinción importante. En `Cancel_publishes_the_refund_computed_by_the_domain`
aparece un `150m` que parece duplicar una regla ya probada:

```csharp
result.Value.RefundAmount.ShouldBe(150m);
cancelled.RefundAmount.ShouldBe(150m);   // el mismo importe, ya dentro del evento
```

No se está reprobando la política de cancelación —de eso se encarga
`CancellationPolicyTests`—. Se comprueba que el importe **que calculó el dominio de
verdad** llega intacto al DTO y al evento de integración, sin perderse ni redondearse
por el camino. Es propagación, no cálculo.

Si el agregado estuviese mockeado, estas pruebas no valdrían nada: comprobarían que
un doble devuelve lo que se le dijo que devolviera.

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

### 1. «A quién se llama»: verificar también lo que NO se llama

```csharp
result.Error.Code.ShouldBe("rental.overlapping");
await _harness.PricingCalculator.DidNotReceive()
    .QuoteAsync(Arg.Any<PricingRequest>(), Arg.Any<CancellationToken>());
```

Si ya se sabe que el vehículo está ocupado, pedir la tarifa es una llamada de red
inútil en cada petición fallida. El dominio no puede detectar eso porque no sabe que
Pricing está al otro lado de la red. Es una prueba de eficiencia además de corrección.

La variante extrema es `Rejects_an_invalid_period_before_touching_any_collaborator`:
un periodo inválido se rechaza **antes** de hablar con nadie.

### 2. «Con qué argumentos»: que la traducción entre capas sea fiel

```csharp
await _harness.PricingCalculator.Received(1).QuoteAsync(
    Arg.Is<PricingRequest>(request =>
        request.VehicleClass == "economy"
        && request.Days == 7
        && request.BaseDailyRate == 30m),
    Arg.Any<CancellationToken>());
```

Si alguien calculara los días con `(End - Start).Days` en lugar de usar
`period.TotalDays`, **el dominio seguiría pasando todas sus pruebas** y el cliente
recibiría una factura equivocada. El error está en el cableado, no en la regla, y
este es el único nivel donde se ve.

En la misma línea, `Uses_the_daily_rate_returned_by_pricing_and_not_the_catalog_one`
congela de quién es la última palabra sobre el precio: Pricing devuelve 99, el
catálogo decía 30, y la renta debe costar 99.

### 3. «En qué orden»: que la secuencia sea segura

```csharp
Received.InOrder(() =>
{
    _harness.Repository.AddAsync(Arg.Any<Rental>(), Arg.Any<CancellationToken>());
    _harness.UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
    _harness.EventPublisher.PublishAsync(...);
});
```

Si se invierten publicar y guardar, y el commit falla, ya se notificó al cliente una
renta que no existe. Fíjate en lo sutil que es: los tres colaboradores se llamaron, y
el estado final del agregado es idéntico en ambos casos. La única diferencia es el
**orden**, y solo una prueba de interacción la ve.

### 4. «Qué se devuelve cuando un colaborador falla»

Los dobles permiten provocar fallos que con los servicios reales serían muy difíciles
de reproducir a voluntad:

```csharp
_harness.PricingCalculator.QuoteAsync(...)
    .Returns<PricingQuote>(_ => throw new ExternalServiceUnavailableException("pricing"));

result.Error.Code.ShouldBe("pricing.unavailable");                 // acabará en 503, no en 500
await _harness.UnitOfWork.DidNotReceive().SaveChangesAsync(...);   // no queda nada a medias
```

Y el caso inverso, que es una decisión de diseño explícita y por tanto merece prueba:

```csharp
// El bus de eventos falla DESPUÉS del commit
result.IsSuccess.ShouldBeTrue();
await _harness.UnitOfWork.Received(1).SaveChangesAsync(...);
```

La renta ya está guardada; perder el evento se registra y se continúa, en lugar de
tirar abajo una operación de negocio ya confirmada. Sin una prueba que lo fije,
alguien «arreglará» ese `catch` en el siguiente refactor.

### Errores esperados no son excepciones

`RentalService` devuelve `Result<RentalDto>`, no lanza. Un vehículo ocupado no es un
fallo del sistema: es una respuesta válida del caso de uso. Las excepciones de
dominio se capturan en la frontera y se traducen a `Result.Failure(código, mensaje)`.
Eso hace que la API tenga un único punto de traducción a HTTP.

### El precio de las pruebas de interacción

Conviene decirlo claro: estas pruebas **se acoplan a la implementación**. Si mañana
se añade una caché delante de Fleet, `Received(1)` fallará aunque el comportamiento
observable sea correcto.

Ese es el precio de poder verificar orden y llamadas, y por eso se usan **solo aquí**,
donde la interacción *es* el comportamiento. En el dominio no aparece ni un doble:
allí lo que importa es el estado resultante.

### Cómo saber si una prueba está en el nivel equivocado

Al leerla, pregúntate: **¿hay aquí un cálculo o una condición de negocio?**

- «48 horas de antelación → 100 % de reembolso» → es una regla → va al dominio.
- «si Fleet devuelve null, el resultado es `vehicle.not_found` y no se guarda nada»
  → es coreografía → va a Application.

Y el reverso también sirve de alarma: si una prueba de dominio necesita un mock,
probablemente el dominio tiene una dependencia que no debería tener.

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

### La idea: aquí no se prueba tu lógica, se prueban tus suposiciones

Los niveles 1 y 2 prueban **código tuyo**. Este nivel prueba **lo que dabas por
supuesto sobre el sistema de otro**, y una suposición equivocada solo la desmiente el
sistema real.

Ejemplo salido de este mismo repositorio. El primer mapeo de concurrencia optimista
fue este, y compilaba sin una sola advertencia:

```csharp
builder.Property(rental => rental.Version)
    .HasColumnName("xmin").HasColumnType("xid")
    .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
```

Un doble lo habría aceptado sin rechistar. PostgreSQL no: `xmin` es una columna de
sistema y la migración generada intentaba **crearla**. El error no estaba en el
razonamiento, sino en lo que se creía que hacía la otra pieza. Eso es lo que atrapa
este nivel y ningún otro.

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

#### Lo que se prueba no es el negocio: es el mapeo

Y el mapeo no es código tuyo. Es SQL que **EF Core genera** a partir de tu
configuración, y no se puede verificar leyéndolo. El caso más claro es la consulta de
solapamiento:

```csharp
var blockingStates = new[] { RentalStatus.Pending, RentalStatus.Confirmed, RentalStatus.Active };

return await context.Rentals.AnyAsync(
    rental => rental.VehicleId == vehicleId                 // tipo fuerte  → uuid
              && blockingStates.Contains(rental.Status)     // enum→string  → IN (...)
              && rental.Period.Start < end                  // owned type   → columna
              && start < rental.Period.End,
    cancellationToken);
```

Tres traducciones nada triviales en cuatro líneas. Si alguna no se soporta, EF lanza
en tiempo de ejecución o —peor todavía— evalúa en cliente y se trae la tabla entera.

Las cinco cosas que se verifican:

| Qué | Cómo se comprueba |
|---|---|
| Los value objects vuelven idénticos | `Money.Of(75.55m, "EUR")` × 4 días → `302.20 EUR` tras el viaje |
| Las columnas opcionales quedan en `NULL` | `FinalTotal` y `RefundAmount` nulos mientras la renta vive |
| El SQL de solapamiento acierta | intersección sí, contiguo no, cancelada no, otro vehículo no |
| La concurrencia optimista funciona | dos contextos → `DbUpdateConcurrencyException` |
| Las fechas vuelven en UTC | `Period.Start.Offset.ShouldBe(TimeSpan.Zero)` |

**Por qué no EF Core InMemory**: no ejecuta SQL, no aplica migraciones, no valida
tipos de columna y no detecta conflictos de concurrencia. Es decir, no puede
comprobar ninguna de las cinco.

#### Concurrencia optimista sin ensuciar el dominio

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
transporte**: tres decisiones que ningún compilador protege.

- El **topic** (`rental-events`).
- La **clave de partición**: el id de la renta, que garantiza orden por renta.
- La **cabecera** `event-type`, que permite enrutar sin deserializar.

El argumento decisivo es este: productor y consumidores viven en **assemblies
distintos** y solo comparten `Shared.Contracts`. Si el productor escribiera la
cabecera como `event-type` y el consumidor la leyera como `eventType`, **todo
compilaría**, todas las pruebas unitarias pasarían, y en producción los mensajes se
descartarían en silencio. Solo un broker real lo detecta.

```csharp
message.Message.Headers.TryGetLastBytes(EventHeaders.EventType, out var typeBytes).ShouldBeTrue();
Encoding.UTF8.GetString(typeBytes).ShouldBe(IntegrationEventTypes.RentalRequested);
```

#### Cómo mirar lo que realmente se publicó

Cuando una prueba sobre Kafka falla, la pregunta suele ser «¿se publicó de verdad, y
con qué forma?». Con la pila levantada, **Kafka UI en http://localhost:5105** responde
eso sin escribir una línea de código: muestra cada mensaje del topic `rental-events`
con su clave de partición, sus cabeceras y su payload, y el estado de los dos grupos
de consumo (`fleet-service` y `notifications-service`) con su *lag*.

Es justo la vista que habría acortado el diagnóstico de los fallos 8 y 11 de la
sección 12: un consumidor parado se ve al instante como un *lag* que crece, y un topic
inexistente, como un topic que sencillamente no aparece en la lista.

Ojo con una limitación: las pruebas de Testcontainers levantan **su propio** broker
efímero en un puerto aleatorio, que no es el de compose. Kafka UI solo ve el clúster
de `docker compose`, así que sirve para depurar el sistema desplegado y las pruebas de
humo y E2E, no las de Testcontainers.

El funcionamiento de la mensajería —el recorrido de un evento, qué significan clave,
offset y grupo de consumo, y cómo leer el *lag*— está en
[`KAFKA.md`](KAFKA.md).

### WireMock.Net para los clientes HTTP

`HttpAdapterTests` prueba la **traducción de protocolo**. La decisión de diseño que
más pesa está en cuatro líneas del adaptador de Fleet:

```csharp
if (response.StatusCode == HttpStatusCode.NotFound) return null;   // no existe NO es un error
if (!response.IsSuccessStatusCode) throw new ExternalServiceUnavailableException("fleet");
```

«No existe» y «no está disponible» son cosas distintas, y esa distinción es la que
después decide si el usuario ve **404** o **503**.

| Situación | Traducción esperada |
|---|---|
| Fleet responde 200 | `VehicleSnapshot` poblado |
| Fleet responde 404 | `null` — no existe **no es** un error |
| Fleet responde 500 | `ExternalServiceUnavailableException` |
| Fleet tarda más que el timeout | `ExternalServiceUnavailableException` |
| Pricing responde 400 | `ExternalServiceUnavailableException` |
| Pricing no escucha | `ExternalServiceUnavailableException` |

Lo que hace útil a WireMock es precisamente la mitad inferior de esa tabla: con el
servicio real es casi imposible provocar un 500 o un timeout cuando quieres. Con
WireMock es una línea.

```csharp
.RespondWith(Response.Create().WithDelay(TimeSpan.FromSeconds(3)).WithStatusCode(HttpStatusCode.OK));
```

### Por qué WireMock aquí y el servicio real en el nivel 4

Es la pregunta que suele surgir al comparar esta sección con la de integración. La
respuesta es que los dos niveles persiguen objetivos **opuestos**:

- El **nivel 3** quiere *control total* sobre el otro lado, incluidos estados que el
  servicio real casi nunca produce.
- El **nivel 4** quiere *realidad*: que Fleet responda lo que de verdad responde.

No es duplicación, son preguntas distintas. Por eso en las pruebas de integración
Pricing sigue simulado —su lógica ya está cubierta y hace falta poder tirarlo a
voluntad— mientras que Fleet es real.

### Las dos pruebas que parecen fuera de sitio

**`ResilienceAndWiringTests`** resuelve los puertos **desde el contenedor de DI real**,
con la misma llamada `AddRentalsInfrastructure(configuration)` que usa la API. Prueba
dos cosas que nadie más ejercita hasta producción: que el registro de dependencias es
correcto, y que la política de reintentos existe y funciona.

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

**`EventContractTests`** es la excepción del nivel: no necesita Docker. Congela los
nombres de los tipos de evento, el nombre del topic y la forma del JSON. Está aquí y
no en el dominio porque esos nombres son del **exterior**: son contrato público, no
lenguaje interno. Si alguien renombra una propiedad, falla aquí en un segundo, y no
en producción tres semanas después.

### El coste, y cómo se paga

Este nivel tarda 44 s frente a los 2 s del dominio. Tres decisiones lo mantienen
manejable:

- Contenedor compartido por colección (`ICollectionFixture<>`), no por prueba.
- `TRUNCATE` entre pruebas en lugar de recrear el contenedor.
- Tiempos de reintento y timeouts configurables.

Y el aviso honesto: **es el nivel que más problemas de entorno da**. De los cuatro
fallos encadenados que se documentan en la sección 12 (puntos 8 a 11), tres estaban
aquí o en su frontera: el hilo del consumidor, el `TRUNCATE` concurrente y el topic
inexistente.

### Cómo saber si algo va aquí

Pregúntate si lo que quieres comprobar es **«¿mi lógica decide bien?»** o
**«¿esto funciona de verdad contra X?»**.

- «El reembolso con 30 h de antelación es del 50 %» → lógica → dominio.
- «El `IN` con enums convertidos a string se traduce a SQL» → contra X → aquí.
- «Un 404 de Fleet significa que el vehículo no existe» → contra X → aquí.

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


**14. Una imagen de contenedor obsoleta degradó el sistema en silencio.**
Tras ampliar `rental.cancelled` con un campo nuevo, se reconstruyeron las imágenes de
los servicios *nuevos* pero no la de `rentals-api`. El productor siguió publicando el
evento **sin** ese campo; Billing lo deserializaba con el valor por defecto (cero),
calculaba una penalización negativa y decidía «no hay nada que facturar». Nada falló:
simplemente dejaron de emitirse facturas.

Las pruebas no lo detectaron porque todas usan el código compilado del momento; el
desajuste solo existe en el despliegue. La lección práctica es que **al cambiar
`Shared.Contracts` hay que reconstruir todos los servicios**, productores incluidos, no
solo los que se acaban de tocar.

**15. Un enum que viajaba como número.**
`PolicyStatus` salía en el JSON como `0`, `1`, `2` en lugar de `Draft`, `Active`… El
frontal habría mostrado un número al usuario. Se corrigió con
`JsonStringEnumConverter`, y la corrección destapó la otra mitad del problema: **el
convertidor también hace falta al leer**. Una prueba que deserializaba `List<Policy>`
empezó a fallar hasta configurarlo también en el cliente, que es exactamente lo que
tendrá que hacer cualquier consumidor de esa API.

**16. Facturar una cancelación que nunca llegó a cobrarse.**
Al escribir la prueba E2E de la factura apareció un fallo de diseño: Billing calculaba
la penalización como `total − reembolso`. Cancelar una renta **antes de confirmarla**
devuelve cero —porque nunca hubo cargo—, así que esa resta daba el total entero y se
facturaba la renta completa a alguien que no había reservado nada en firme.

El evento no permitía distinguir «cancelada sin cargo» de «cancelada con 0 % de
reembolso». La corrección fue mover la decisión al sitio que sí tiene la información:
el agregado calcula ahora un `PenaltyAmount` explícito y Billing se limita a
facturarlo. Está cubierto por una `[Theory]` de dominio que comprueba, en cada tramo
de la política, que **lo reembolsado más lo cobrado suma siempre el total**.

---

## 13. Dónde poner una prueba nueva

| Si quieres comprobar… | Va en… |
|---|---|
| Una regla de negocio de la renta | `Rentals.Domain.Tests` |
| Una regla de la factura | `Billing.Domain.Tests` |
| Que el caso de uso llama a los puertos correctos | `Rentals.Application.Tests` / `Billing.Application.Tests` |
| Un código de estado HTTP o la forma del JSON | `Rentals.Api.Tests` (Minimal API) / `Billing.Api.Tests` (controllers) |
| El cálculo de una prima o el ciclo de vida de una póliza | `Insurances.Api.Tests` |
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

### Nivel 1 o nivel 2: la duda más frecuente

La frontera entre dominio y aplicación es la que más cuesta, porque casi todo suena a
«regla de negocio». La pregunta que de verdad discrimina es otra:

> **¿El agregado tiene dentro todo el dato necesario para decidir?**
>
> - **Sí** → la regla vive en el dominio → **nivel 1**.
> - **No, hay que ir a buscarlo fuera** → la decisión es orquestación → **nivel 2**.

Dos reglas que suenan igual y caen en lados distintos:

- «La licencia debe cubrir el periodo» → licencia y periodo están en el agregado →
  **nivel 1**.
- «El vehículo debe estar disponible» → esa información vive en Fleet → **nivel 2**.

Un agregado solo puede razonar sobre lo que tiene. En cuanto hay que preguntarle a
alguien, deja de ser una decisión del dominio.

#### Tres comprobaciones rápidas

| | Nivel 1 | Nivel 2 |
|---|---|---|
| ¿Necesitas un doble? | No | Sí |
| ¿Qué instancias en el `arrange`? | `RentalBuilder.A()` | `new RentalService(...)` |
| ¿Qué afirmas? | un **valor**: total, estado, excepción | un **suceso**: a quién se llamó, en qué orden, qué pasa si falla |

El corolario de la primera fila importa más que la fila: si una prueba de dominio
necesitara un mock, el problema no es la prueba, es que el dominio tiene una
dependencia que no debería tener.

#### El patrón: la regla abajo, la conexión arriba

| Situación | Nivel 1 pregunta | Nivel 2 pregunta |
|---|---|---|
| Licencia vencida | ¿lanza `DriverLicenseExpiredException`? | ¿se convierte en `Result.Failure`? |
| Precio | ¿50 × 3 = 150? | ¿se usa la tarifa de Pricing y no la del catálogo? |
| El «ahora» | ¿rechaza un periodo ya iniciado? | ¿ese `now` sale del puerto `IClock`? |

#### Un concepto puede repartirse entre niveles

El solapamiento es el mejor ejemplo: aparece en tres, y ninguno repite a otro.

```csharp
Overlaps_is_false_for_back_to_back_periods              // nivel 1: QUÉ es solaparse
Fails_when_another_rental_overlaps_the_period           // nivel 2: qué hace el caso de uso
HasOverlappingRental_is_false_for_a_back_to_back_period // nivel 3: que el SQL lo implemente
```

El dominio define el concepto, la aplicación lo aplica sobre datos traídos de fuera,
y el adaptador comprueba la búsqueda contra PostgreSQL.

#### Señal de que te equivocaste de nivel

En una prueba de aplicación, **un número en el assert que no proviene de ningún
doble**. Si `250m` lo calculó el dominio, esa prueba iba abajo.

La excepción legítima es la propagación: en
`Cancel_publishes_the_refund_computed_by_the_domain` el `150m` se afirma también
**dentro del evento publicado**, y lo que se comprueba es que el importe atraviesa
DTO y evento sin perderse. Si al borrar esa segunda aserción la prueba sigue teniendo
sentido, estabas duplicando.
