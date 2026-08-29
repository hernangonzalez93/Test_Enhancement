# Kafka en TestEnforce

Cómo funciona la mensajería de este sistema y cómo observarla mientras corre.

No es un tutorial genérico de Kafka: todos los ejemplos, números y capturas salen de
la pila real de este repositorio, levantada con `docker compose up -d --wait`.

Para la estrategia de pruebas sobre Kafka, ver
[`TESTING.md`](TESTING.md) (secciones 6 y 8).

---

## 1. El modelo mental: Kafka no es una cola

Una cola entrega un mensaje y lo borra. Kafka es un **log de solo-anexado**: los
mensajes se escriben al final y **se quedan ahí**, los lea quien los lea. Leer no
consume nada; solo mueve tu marcador de posición.

Esa diferencia es la que permite que dos servicios lean los mismos mensajes sin
estorbarse.

```
topic rental-events  (partición 0)

offset:  0    1    2   ...  47   48   49
        [ ][ ][ ] ............ [ ][ ][ ]
                                ▲         ▲
                    fleet-service         final del log
                    notifications-service
```

---

## 2. Las cinco piezas, en este sistema

| Pieza | Aquí | Para qué sirve |
|---|---|---|
| **Topic** | `rental-events` | El canal. Uno solo para todos los eventos de renta |
| **Partición** | 1 | Unidad de orden y de paralelismo |
| **Clave** | `rentalId` | Decide la partición → garantiza orden por renta |
| **Offset** | 0 → 49 | Posición en el log. Solo crece |
| **Grupo de consumo** | `fleet-service`, `notifications-service`, `insurances-service`, `billing-service` | Cada uno con **su propio** marcador |

### El grupo de consumo es la pieza clave

Cada grupo lleva su offset por separado, así que los **cuatro** consumidores reciben
cada uno una copia de todos los eventos. Eso es el *fan-out*, y es lo que permite
añadir un servicio nuevo —Insurances y Billing se sumaron después— sin tocar una sola
línea de Rentals.

Si ambos compartieran `GroupId`, se **repartirían** los mensajes y cada evento lo
vería solo uno de los dos. Una línea de configuración separa un comportamiento del
otro:

```csharp
GroupId = "fleet-service"           // Fleet.Api          -> disponibilidad del vehiculo
GroupId = "notifications-service"   // Notifications.Api  -> avisos al cliente
GroupId = "insurances-service"      // Insurances.Api     -> polizas
GroupId = "billing-service"         // Billing.Api        -> facturas
```

### La clave no es un identificador cualquiera

```csharp
public string PartitionKey => RentalId.ToString();
```

Todos los eventos de una misma renta caen en la misma partición y por tanto se leen
**en orden**. Con una sola partición todo está ordenado igual; la clave es lo que hace
que siga funcionando el día que se escale a seis particiones.

La sección siguiente desarrolla estas dos piezas —clave y partición— con
demostraciones reales: por qué en la UI todo aparece en la partición 0, cómo se
reparten las claves, quién decide qué lee cada consumidor y qué ocurre en un
rebalanceo.

---

## 3. Particiones, claves y grupos: quién decide qué

Esta es la parte que más confusión genera, así que va con demostraciones reales.

### Por qué en la UI todo aparece en la partición 0

Porque el topic tiene **una sola partición**. No hay ninguna otra a la que ir.

```csharp
new TopicSpecification
{
    Name = _options.Topic,
    NumPartitions = 1,        // ← aquí
    ReplicationFactor = 1
}
```

La clave no está fallando: Kafka elige partición con
`murmur2(clave) % nº_particiones`, y cualquier cosa `% 1` es siempre 0. La clave
no tiene dónde elegir.

### La clave sí decide, pero de muchos a uno

Con un topic desechable de 3 particiones y claves distintas, el reparto real fue:

```
alfa                                      → partición 1
bravo, delta, echo, hotel, india, lima    → partición 2
charlie, foxtrot, golf, juliet, kilo      → partición 0

renta-A  (3 mensajes)                     → los 3 a la partición 0
renta-B  (2 mensajes)                     → los 2 a la partición 0
```

Las dos propiedades que importan quedan visibles:

- **Claves distintas se reparten** entre las particiones.
- **La misma clave cae siempre en la misma partición** — los tres mensajes de
  `renta-A` juntos. Eso es lo que garantiza el orden por renta.

Y la consecuencia que suele sorprender: **una partición contiene muchísimas claves
distintas**, mezcladas e intercaladas en el mismo log.

```
partición 0:  [renta-A ev1][renta-K ev1][renta-A ev2][renta-Z ev1][renta-A ev3]
                    ▲                        ▲                         ▲
                    └──── el orden de renta-A entre sí se conserva ─────┘
```

Kafka **no** garantiza nada sobre el orden entre `renta-A` y `renta-K`. Solo que los
eventos de una misma renta se leen como se escribieron, que es lo único que este
sistema necesita: que `rental.requested` llegue antes que `rental.confirmed` **de la
misma renta**.

| | |
|---|---|
| Un `rentalId` → | exactamente **una** partición, siempre la misma |
| Una partición → | **muchos** `rentalId` distintos, entremezclados |
| El número de partición | no significa nada: es un hash, no un identificador |

> Anécdota útil: al preparar esta demostración se usaron primero las claves
> `renta-A` … `renta-E` y **las cinco cayeron en la partición 0**, lo que parecía
> indicar que el particionado no funcionaba. Era casualidad (~0,4 % de probabilidad)
> con cinco cadenas casi idénticas. Con doce claves bien distintas el reparto apareció
> de inmediato. Moraleja: con pocas claves el hash reparte de forma muy desigual —el
> reparto real fue 13 / 1 / 6—; con miles de ids se iguala.

### El consumidor no busca: le asignan

Aquí hay un cambio de modelo mental. Kafka no es una base de datos que consultas. **No
existe** «dame los eventos de la renta X». En todo el consumidor no aparece la palabra
partición:

```csharp
consumer.Subscribe(_options.Topic);                              // "quiero este topic entero"
var result = consumer.Consume(TimeSpan.FromMilliseconds(500));   // "dame lo siguiente"
```

Lee lo que venga, en orden, y descarta lo que no le interesa:

```csharp
var (vehicleId, available) = integrationEvent switch
{
    RentalConfirmedIntegrationEvent e => (e.VehicleId, (bool?)false),
    RentalCancelledIntegrationEvent e => (e.VehicleId, (bool?)true),
    _ => (Guid.Empty, null)          // no me interesa, siguiente
};
```

No hace falta buscar porque acabará viendo todos los mensajes de las particiones que
tenga asignadas. Y quien asigna es el **coordinador del grupo**, dentro del broker. En
los logs se ve:

```
Subscribed to rental-events as fleet-service.
Partitions assigned: 0
```

Ese segundo mensaje es el callback que se añadió al arreglar el fallo 8 de la
sección 12 de [`TESTING.md`](TESTING.md), y es también la señal de readiness:

```csharp
.SetPartitionsAssignedHandler((_, partitions) =>
{
    readiness.MarkReady();          // hasta aquí, /health/ready falla
    logger.LogInformation("Partitions assigned: {Partitions}", ...);
})
```

Buscar una renta concreta es una necesidad de **depuración**, no del sistema: se hace
con el filtro por clave de Kafka UI, o —en las pruebas— leyendo todo y filtrando en
memoria.

### Los offsets son por partición

No existe «el offset del topic». El offset es una posición **dentro de una
partición**, y la salida del CLI lo dice literalmente:

```
rental-events:0:52
      ▲        ▲  ▲
   topic  partición offset
```

Cada partición numera desde cero de forma independiente:

```
demo-particiones:0:13
demo-particiones:1:1
demo-particiones:2:6
```

Un mensaje se identifica de forma única por la terna **(topic, partición, offset)**.

El grupo también guarda su posición por partición. Por eso el comando de grupos tiene
una columna `PARTITION`, y con 3 particiones daría **tres filas**:

```
GROUP           TOPIC           PARTITION  CURRENT-OFFSET  LOG-END-OFFSET  LAG
fleet-service   rental-events   0          31              31              0
fleet-service   rental-events   1          10              14              4     ← retrasada
fleet-service   rental-events   2          11              11              0
```

Eso es muy útil al diagnosticar: si solo una partición acumula lag, no hay un problema
de capacidad general, sino una instancia atascada o una clave «caliente». El lag que
muestra Kafka UI por grupo es la **suma** de los lags por partición.

Esas posiciones viven en el topic interno **`__consumer_offsets`**, con la clave
**(grupo, topic, partición)**. Fíjate en lo que *no* forma parte de esa clave: la
instancia. A Kafka le da igual qué proceso leyó qué.

### El rebalanceo: cómo sabe la instancia nueva por dónde iba

No lo sabe *ella*: lo sabe el broker.

```
Antes:   instancia A → particiones 0, 1
         instancia B → partición 2

Se cae A:

1. El coordinador detecta que A dejó de enviar heartbeats
2. REVOKE   → se retiran las asignaciones
3. ASSIGN   → B recibe 0, 1 y 2
4. B pregunta el offset confirmado de cada partición
5. B reanuda: partición 0 desde 52, partición 1 desde 31, partición 2 desde 47
```

B no hereda nada de A; hereda **del grupo**. Es exactamente el mismo mecanismo por el
que, al parar y arrancar `notifications-api` en la demostración del lag, el servicio
retomó donde iba: su posición no estaba en la memoria del contenedor, estaba en el
broker.

Y aquí está el hueco que obliga a la idempotencia. Con `EnableAutoCommit = true` la
posición se confirma cada ~5 s, no tras cada mensaje:

```
mensaje 31  procesado ✓   confirmado ✗
mensaje 32  procesado ✓   confirmado ✗     ← A muere aquí
mensaje 33  procesado ✓   confirmado ✗
                            │
                            └─ el broker sigue creyendo que el grupo va por 31
```

B reanuda en 31 y **reprocesa 31, 32 y 33**. Dicho de otro modo: **un consumidor no
sabe con certeza cuáles ya procesó, solo cuáles quedaron confirmados**. Por eso los
dos handlers toleran repeticiones (ver la sección 6).

### Cuándo querrías más de una partición

El motivo no es el rendimiento de escritura, es el **paralelismo de consumo**:

> Dentro de un grupo, **cada partición la lee como máximo un consumidor**.

Con una partición, escalar `fleet-service` a 3 réplicas dejaría a dos sin trabajo. Para
que tres instancias trabajen a la vez hacen falta al menos 3 particiones.

Al pasar a N particiones se pierde el orden global, pero **se conserva el que importa**:
como la clave es el `rentalId`, los eventos de una misma renta siguen cayendo juntos.
Para eso está esa clave.

Aquí una sola partición es una decisión deliberada: da orden global gratis, es lo más
simple de razonar y de probar, y no hay volumen que justifique más.

### Si quieres cambiarlo

```bash
docker exec te-kafka kafka-topics --bootstrap-server kafka:29092 --alter --topic rental-events --partitions 3
```

Dos avisos:

1. Es **irreversible**: no se pueden reducir particiones después.
2. Los mensajes ya escritos **se quedan donde están**. Una renta con eventos antiguos
   en la partición 0 podría tener los nuevos en la 2, rompiendo su orden. En producción
   esto se hace en una ventana sin tráfico, o creando un topic nuevo.

Para experimentar en local es seguro: `docker compose down -v` deja todo como nuevo.

---

## 4. El recorrido de un evento

```
POST /api/rentals/{id}/confirm
   │
   ├─ 1. RentalService carga la renta y llama a rental.Confirm(now)
   │        el dominio valida la transición y registra un evento interno
   ├─ 2. SaveChangesAsync()            ← primero se guarda
   └─ 3. KafkaEventPublisher.PublishAsync()
            ProduceAsync(topic:   "rental-events",
                         key:     rentalId,
                         value:   JSON,
                         headers: event-type = "rental.confirmed")
                 │
                 ▼   el log crece: offset 48 → 49
         ┌───────┴────────┐
         ▼                ▼
   fleet-service    notifications-service      ← dos grupos, independientes
   marca el         guarda la notificación
   vehículo         para el cliente
   como rentado
```

El orden de los pasos 2 y 3 es deliberado y está blindado con una prueba
(`Persists_and_commits_before_publishing`): publicar antes del commit permitiría
notificar una renta que después no se guarda.

---

## 5. Cómo viaja un mensaje, literalmente

Esto es una captura real del topic, tomada con el consumidor de consola:

```
event-type:rental.confirmed,event-id:01a04df6-85b4-7f02-84ac-76aee370d4ef
01a04df6-825f-7cbf-8423-4d213aa9d980
{"rentalId":"01a04df6-825f-7cbf-8423-4d213aa9d980",
 "customerId":"cce05294-5244-440e-9a2e-656cb9232fe2",
 "vehicleId":"01a04dec-bbde-72e0-992b-a482a18e53dd",
 "estimatedTotal":60.00,"currency":"USD",
 "occurredAt":"2026-08-29T14:39:56.8357547+00:00",
 "eventId":"01a04df6-85b4-7f02-84ac-76aee370d4ef",
 "eventType":"rental.confirmed",
 "partitionKey":"01a04df6-825f-7cbf-8423-4d213aa9d980"}
```

Tres partes:

- **Cabeceras**: `event-type` y `event-id`. La primera es la que permite a los
  consumidores **enrutar sin deserializar** el cuerpo. Si algún día ves un mensaje sin
  ella, ese es el bug.

Los cinco tipos originales (`rental.requested`, `confirmed`, `started`, `completed`,
`cancelled`) se ampliaron con **`rental.extended`** al añadir la prórroga: Insurances
lo necesita para alargar la vigencia de la póliza. Dejarlo como evento interno habría
dejado pólizas que caducan antes que la renta que aseguran.

`rental.cancelled` lleva además un **`penaltyAmount`** calculado por el dominio. Antes
Billing lo derivaba como `total − reembolso`, y eso facturaba la renta entera cuando se
cancelaba **antes de confirmar** —donde el reembolso es cero porque nunca hubo cargo—.
Quien tiene la información para decidirlo es el agregado, así que la decide él (ver el
punto 16 de la sección 12 de [`TESTING.md`](TESTING.md)).
- **Clave**: el `rentalId`.
- **Valor**: el JSON en camelCase.

> Observación honesta: `eventType` y `partitionKey` aparecen **también** dentro del
> JSON, porque son propiedades calculadas del record y el serializador las incluye.
> Es información redundante en el cable —la cabecera y la clave ya la llevan— pero
> inofensiva. Marcarlas con `[JsonIgnore]` sería una mejora menor.

---

## 6. Las decisiones de diseño de este proyecto

| Decisión | Configuración | Por qué |
|---|---|---|
| Un solo topic | `rental-events` | Todos los eventos de una renta comparten orden. Separarlos por tipo rompería esa garantía |
| Clave = id de renta | `PartitionKey` | Orden por renta, y reparto uniforme entre particiones |
| Confirmación total | `Acks.All` | El broker responde solo cuando el mensaje está replicado |
| Productor idempotente | `EnableIdempotence = true` | Un reintento del productor no duplica el mensaje |
| Desde el principio | `AutoOffsetReset.Earliest` | Un grupo nuevo lee todo el historial en vez de empezar en «ahora» |
| Confirmación automática | `EnableAutoCommit = true` | Simple; a cambio obliga a que los consumidores sean idempotentes (ver abajo) |
| Topic creado al arrancar | `EnsureTopicExists()` | No depender de `auto.create.topics.enable`, que en clústeres reales suele estar apagado |

### Entrega «al menos una vez», y qué implica

Con `EnableAutoCommit = true` los offsets se confirman cada ~5 s, no tras cada
mensaje. Existe por tanto una ventana en la que el trabajo ya está hecho pero la
posición aún no está confirmada: si el proceso muere ahí, al arrancar **reprocesa**
esos mensajes.

Por eso **los dos consumidores son idempotentes por diseño**, cada uno a su manera.

Fleet compara contra el estado actual:

```csharp
if (vehicle.Available == available.Value)
{
    return false;   // reprocesar el mismo evento no produce ningún efecto adicional
}
```

Notifications deduplica por identidad del evento. El `EventId` viaja en el mensaje y
en la cabecera `event-id`, y es estable entre reprocesos, así que se usa como Id de la
notificación:

```csharp
private static Notification Build(IIntegrationEvent source, ...) =>
    new(source.EventId, rentalId, customerId, source.EventType, message, source.OccurredAt);
```

```csharp
return Task.FromResult(_notifications.TryAdd(notification.Id, notification));   // false si ya estaba
```

Puedes comprobarlo en el sistema desplegado: el `id` que devuelve
`GET /api/notifications` coincide exactamente con la cabecera `event-id` del mensaje
en Kafka.

Cubierto por `Reprocessing_the_same_event_changes_nothing` (Fleet) y
`Reprocessing_the_same_event_stores_a_single_notification` (Notifications).

---

## 7. El lag: la métrica que lo explica casi todo

> **lag = último offset del topic − offset confirmado por el grupo**
>
> Es decir: cuántos mensajes te faltan por procesar.

Esta es una ejecución real sobre esta pila, parando un consumidor a propósito:

```
1) Estado inicial (offsetMax=40)
   fleet: lag=0 | notifications: lag=0

2) docker compose stop notifications-api

3) Se crea una renta, se confirma y se cancela  →  offsetMax=43
   fleet: lag=3 | notifications: lag=3

4) docker compose start notifications-api
   fleet: lag=0 | notifications: lag=3     ← pero ya tenía las 3 notificaciones

5) Dos segundos después
   fleet: lag=0 | notifications: lag=0
```

Dos lecciones, y la segunda es la interesante:

**El consumidor parado no perdió nada.** Los mensajes seguían en el log; al volver
retomó desde su offset y recuperó las tres notificaciones. Eso es lo que un sistema de
colas tradicional no da gratis.

**El lag mide offsets *confirmados*, no mensajes *procesados*.** En el paso 4 el
trabajo estaba hecho pero la posición todavía no confirmada, por el intervalo de
`EnableAutoCommit`. Es la misma ventana que obliga a la idempotencia de la sección
anterior.

---

## 8. La interfaz web

**http://localhost:5105** (`kafbat/kafka-ui`)

| Pantalla | Qué te dice |
|---|---|
| **Brokers** | Que el clúster vive. 1 broker, modo KRaft, sin ZooKeeper |
| **Topics → rental-events → Overview** | Particiones y offsets mínimo/máximo. El `offsetMax` es cuántos eventos se han publicado en total |
| **Topics → rental-events → Messages** | **La pantalla que más se usa.** Cada mensaje con clave, cabeceras y payload; se puede filtrar por clave, offset o fecha |
| **Topics → rental-events → Consumers** | Qué grupos leen este topic y por qué offset van |
| **Consumers** | Los dos grupos, su estado (`STABLE` = todo bien) y su lag |

La UI no declara `healthcheck` en compose a propósito: es Java y tarda unos 30 s en
arrancar, y hacer que `up --wait` la espere añadiría ese tiempo a cada ciclo de
pruebas por una herramienta que las pruebas no usan. Si abres la página justo después
de levantar la pila, puede tardar un poco en responder.

Si estorba: `docker compose stop kafka-ui`. El resto del sistema no se entera.

---

## 9. Tres experimentos para ver cosas moverse

### a) Ver nacer un evento

Deja abierto *Messages* del topic, ve al frontend (http://localhost:5173), crea una
renta y refresca: aparece `rental.requested`. Confírmala → `rental.confirmed`.
Cancélala → `rental.cancelled`. Los tres comparten la misma clave y salen en orden.

### b) Ver el fan-out

En *Consumers*, los dos grupos van por el mismo offset y ambos con lag 0. Son dos
lectores independientes del mismo log.

### c) Provocar y curar el lag

```bash
docker compose stop notifications-api
```

Crea un par de rentas desde el frontend y mira *Consumers*: el lag de
`notifications-service` crece mientras el de `fleet-service` sigue en 0. Después:

```bash
docker compose start notifications-api
```

El lag vuelve a 0 y las notificaciones aparecen en la pantalla de la renta. No se
perdió nada.

---

## 10. Lo mismo desde la línea de comandos

Todos verificados contra esta pila.

Describir el topic:

```bash
docker exec te-kafka kafka-topics --bootstrap-server kafka:29092 --describe --topic rental-events
```

Ver el lag de un grupo:

```bash
docker exec te-kafka kafka-consumer-groups --bootstrap-server kafka:29092 --describe --group fleet-service
```

```
GROUP          TOPIC          PARTITION  CURRENT-OFFSET  LOG-END-OFFSET  LAG
fleet-service  rental-events  0          49              49              0
```

> Aviso si usas **Git Bash** en Windows: convierte automáticamente los argumentos que
> empiezan por `/` en rutas de Windows, así que `docker exec ... /opt/kafka/bin/algo.sh`
> se transforma en `C:/Program Files/Git/opt/...` y falla. Se evita duplicando la barra
> (`//opt/...`) o ejecutando desde PowerShell. Los comandos de esta sección no lo sufren
> porque invocan los binarios por nombre, que están en el `PATH` de la imagen.

Leer los mensajes con su clave y sus cabeceras:

```bash
docker exec te-kafka kafka-console-consumer --bootstrap-server kafka:29092 --topic rental-events --from-beginning --max-messages 5 --property print.key=true --property print.headers=true
```

---

## 11. Varios Kafka a la vez: cuál estás mirando

Ejecutar este repositorio puede dejar **tres brokers distintos** funcionando en el
mismo equipo:

| Broker | Puerto en el host | Vida | Quién lo ve |
|---|---|---|---|
| El de `docker compose` | **59092** | Mientras la pila esté levantada | Kafka UI :5105, humo, E2E |
| Los efímeros de **Testcontainers** | Puerto aleatorio | Solo durante una ejecución de pruebas | Únicamente el código de la prueba |
| Cualquier otra pila local | Normalmente 9092 | Ajena a este repositorio | Sus propias herramientas |

Por eso este compose publica PostgreSQL en 55432 y Kafka en 59092 en lugar de los
puertos estándar, y Kafka UI en 5105 en lugar de 8080: para **convivir** con otras
pilas en vez de disputarles el puerto.

Dos consecuencias prácticas:

- **Kafka UI solo ve el clúster de compose.** Sirve para depurar el sistema desplegado,
  las pruebas de humo y las E2E. **No** verás ahí lo que hacen
  `Rentals.Infrastructure.Tests` ni `Rentals.Integration.Tests`: esas inspeccionan los
  mensajes desde el propio código de la prueba, con un consumidor creado al efecto.
- **Si una UI de Kafka no muestra lo que esperas, comprueba primero a qué broker apunta.**
  Un `rental-events` vacío suele significar que estás mirando el clúster equivocado, no
  que la publicación haya fallado.

Las pruebas de integración van más allá: además de su propio broker usan un **topic con
nombre único por ejecución** y **grupos de consumo únicos**, para que dos ejecuciones
simultáneas no se pisen ni dentro del mismo contenedor.
