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
| **Grupo de consumo** | `fleet-service`, `notifications-service` | Cada uno con **su propio** marcador |

### El grupo de consumo es la pieza clave

Cada grupo lleva su offset por separado, así que Fleet y Notifications reciben **cada
uno** una copia de todos los eventos. Eso es el *fan-out*.

Si ambos compartieran `GroupId`, se **repartirían** los mensajes y cada evento lo
vería solo uno de los dos. Una línea de configuración separa un comportamiento del
otro:

```csharp
GroupId = "fleet-service"           // Fleet.Api
GroupId = "notifications-service"   // Notifications.Api
```

### La clave no es un identificador cualquiera

```csharp
public string PartitionKey => RentalId.ToString();
```

Todos los eventos de una misma renta caen en la misma partición y por tanto se leen
**en orden**. Con una sola partición todo está ordenado igual; la clave es lo que hace
que siga funcionando el día que se escale a seis particiones.

---

## 3. El recorrido de un evento

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

## 4. Cómo viaja un mensaje, literalmente

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
- **Clave**: el `rentalId`.
- **Valor**: el JSON en camelCase.

> Observación honesta: `eventType` y `partitionKey` aparecen **también** dentro del
> JSON, porque son propiedades calculadas del record y el serializador las incluye.
> Es información redundante en el cable —la cabecera y la clave ya la llevan— pero
> inofensiva. Marcarlas con `[JsonIgnore]` sería una mejora menor.

---

## 5. Las decisiones de diseño de este proyecto

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

## 6. El lag: la métrica que lo explica casi todo

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

## 7. La interfaz web

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

## 8. Tres experimentos para ver cosas moverse

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

## 9. Lo mismo desde la línea de comandos

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

Leer los mensajes con su clave y sus cabeceras:

```bash
docker exec te-kafka kafka-console-consumer --bootstrap-server kafka:29092 --topic rental-events --from-beginning --max-messages 5 --property print.key=true --property print.headers=true
```

---

## 10. Una limitación importante

Las pruebas con **Testcontainers levantan su propio broker efímero** en un puerto
aleatorio, distinto del de compose. Kafka UI solo ve el clúster de `docker compose`.

Sirve, por tanto, para depurar el sistema desplegado, las pruebas de humo y las E2E.
**No** verás ahí lo que hacen `Rentals.Infrastructure.Tests` ni
`Rentals.Integration.Tests`: para esas, los mensajes se inspeccionan desde el propio
código de la prueba, con un consumidor creado al efecto.
