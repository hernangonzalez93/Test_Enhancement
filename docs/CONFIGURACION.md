# Configuración en TestEnforce

Cómo se resuelve un valor de configuración, en qué orden mandan las fuentes y qué
consideraciones hay al llevarlo a contenedores.

No es un tutorial genérico de `Microsoft.Extensions.Configuration`: todos los ejemplos
salen de ficheros reales de este repositorio.

---

## 1. El modelo mental: no es un fichero, es un diccionario

La configuración **no es `appsettings.json`**. Es un diccionario plano que se construye
apilando fuentes, y del que `appsettings.json` es solo una.

Cada proveedor aporta pares clave/valor y **el último registrado gana, clave por
clave**. Las claves son planas, con `:` como separador de jerarquía. Este JSON de
[`Billing.Api/appsettings.json`](../src/Billing/Billing.Api/appsettings.json):

```json
{
  "Database": {
    "AutoMigrate": false
  }
}
```

no produce un objeto anidado, sino **una sola entrada**:

```
Database:AutoMigrate = "false"
```

Y el valor es la cadena `"false"`, no el booleano. Todos los valores del diccionario
son cadenas; la conversión de tipo ocurre al leerlos:

```csharp
app.Configuration.GetValue<bool>("Database:AutoMigrate")
```

Entender esto explica casi todo lo demás: si es un diccionario plano de cadenas,
cualquier fuente capaz de producir pares texto/texto —variables de entorno, argumentos
de línea de comandos, una tabla de una base de datos— sirve como proveedor.

---

## 2. El orden por defecto

`WebApplication.CreateBuilder(args)` registra estos proveedores, **de menor a mayor
prioridad**:

| # | Fuente | Notas |
|---|---|---|
| 1 | Configuración del *host* | Variables `ASPNETCORE_*` y `DOTNET_*`, ya sin el prefijo |
| 2 | `appsettings.json` | Se compila dentro de la imagen |
| 3 | `appsettings.{Environment}.json` | Solo si el fichero existe |
| 4 | *User secrets* | **Solo** en el entorno `Development` |
| 5 | **Variables de entorno** | Sin prefijo |
| 6 | Argumentos de línea de comandos | Lo que gana a todo |

Lo que más se malinterpreta: **la sobrescritura es por clave, no por fichero**.

En este repositorio, [`Rentals.Api`](../src/Rentals/Rentals.Api/) tiene los dos
ficheros. `appsettings.Development.json` **no reemplaza** al base: solo pisa las claves
que menciona, y todo lo demás se sigue leyendo del `appsettings.json`. Son capas, no
alternativas.

---

## 3. Los dos puentes que usa este repositorio

### `__` se convierte en `:`

Los dos puntos no son válidos en nombres de variable de entorno en muchos shells, así
que el proveedor traduce el doble guion bajo. En
[`docker-compose.yml`](../docker-compose.yml):

```yaml
Database__AutoMigrate: "true"                    # -> Database:AutoMigrate
Kafka__BootstrapServers: kafka:29092             # -> Kafka:BootstrapServers
ConnectionStrings__BillingDatabase: Host=postgres;Port=5432;...
```

Esas tres líneas son exactamente el mecanismo por el que la misma imagen sirve para
desarrollo local y para producción: **el binario no cambia, cambia la capa de encima**.

Para listas, el índice es un segmento más:

```
Kafka__Brokers__0=broker-uno:9092
Kafka__Brokers__1=broker-dos:9092
```

### `ConnectionStrings:` es una convención

No hay nada especial en esa sección. Esto:

```csharp
builder.Configuration.GetConnectionString("BillingDatabase")
```

es literalmente un atajo de:

```csharp
builder.Configuration["ConnectionStrings:BillingDatabase"]
```

Por eso `ConnectionStrings__BillingDatabase` en el compose funciona sin que nadie lo
haya cableado.

---

## 4. Cómo lo aprovechan las pruebas

En [`FleetFixture.cs`](../tests/Fleet.Api.Tests/FleetFixture.cs):

```csharp
builder.UseEnvironment("Testing");
builder.UseSetting("ConnectionStrings:FleetDatabase", connectionString);
builder.UseSetting("Database:AutoMigrate", "false");
```

Tres decisiones deliberadas:

- **`UseSetting` escribe en la configuración del *host***, que en
  `WebApplicationFactory` se aplica por encima de todo lo demás. Es lo que permite
  inyectar la cadena de conexión del contenedor de Testcontainers, que **no se conoce
  hasta el momento de ejecutar**: no hay fichero que pudiera contenerla.
- **`UseEnvironment("Testing")`** evita que se carguen `appsettings.Development.json`
  y los *user secrets*. La prueba parte de un estado conocido y no depende de lo que
  cada desarrollador tenga en su máquina.
- **`Database:AutoMigrate` a `false`** porque el esquema ya lo ha creado el fixture; la
  aplicación no debe volver a migrar al arrancar.

En [`InvoicesControllerTests.cs`](../tests/Billing.Api.Tests/InvoicesControllerTests.cs)
se usa lo mismo para apagar el consumidor de Kafka (`Kafka:Enabled` a `false`), de modo
que la API arranca en memoria sin necesitar broker.

Que estas pruebas puedan existir **es consecuencia directa** de que ningún valor esté
incrustado en el código.

---

## 5. Consideraciones

**Las claves son insensibles a mayúsculas; los nombres de variable en Linux, no.**
`Database:automigrate` y `Database:AutoMigrate` son la misma clave. Pero definir
`database__automigrate` en un contenedor Linux sí funciona, mientras que en algunos
scripts intermedios puede no sobrevivir. Es una fuente clásica de «en mi máquina va».

**Los JSON se recargan solos; las variables de entorno no.** `reloadOnChange` viene
activado para los `appsettings`, pero un contenedor no cambia sus variables en
caliente: para cambiar una hay que recrear la tarea. En la nube esto es una ventaja —
el cambio de configuración queda registrado como un despliegue nuevo, no como una
mutación invisible.

**Nunca metas secretos en `appsettings.json`.** Ese fichero se compila dentro de la
imagen y viaja al registro; quien pueda descargar la imagen puede leerlo. Las
contraseñas que hay hoy en este repositorio (`Password=billing` y compañía) son
credenciales de desarrollo para el PostgreSQL del compose, y deben quedarse ahí. En
desarrollo, `dotnet user-secrets`; en la nube, un almacén de secretos inyectado como
variables de entorno.

**Ata la configuración a un objeto y valídala al arrancar.**

```csharp
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection("Database"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

`ValidateOnStart()` es la parte que importa: sin él, un valor inválido no falla hasta la
primera petición que lo use, probablemente en producción y probablemente de noche. Con
él, el proceso no llega a declararse sano.

Es el mecanismo que impide que un servicio arranque en producción con la cadena de
conexión de desarrollo: una regla que rechace `localhost` cuando el entorno no es
`Development` convierte un error de despliegue silencioso en un contenedor que
directamente no pasa la sonda de salud.

---

## 6. Resumen

| Pregunta | Respuesta |
|---|---|
| ¿Qué es la configuración? | Un diccionario plano de cadenas, no un fichero |
| ¿Quién gana? | La última fuente registrada, clave por clave |
| ¿Cómo se anida en una variable de entorno? | Con `__`, que se traduce a `:` |
| ¿Dónde van los secretos? | *User secrets* en local; un almacén de secretos en la nube |
| ¿Cuándo se detecta un valor inválido? | Al arrancar, si usas `ValidateOnStart()` |
