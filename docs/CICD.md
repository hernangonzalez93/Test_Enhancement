# CI/CD en TestEnforce

Cómo se verifica y se publica este repositorio. Se construye por fases; este
documento crece con ellas.

| Fase | Estado |
|---|---|
| 1. Integración continua | **Hecha** — este documento |
| 2. Endurecer las imágenes | Pendiente |
| 3. Infraestructura con Terraform | Pendiente |
| 4. Primer despliegue con OIDC | Pendiente |
| 5. El resto de la pila | Pendiente |
| 6. Verificación y vuelta atrás | Pendiente |

---

## 1. El modelo: releases y despliegues no son lo mismo

Es la confusión más común al montar CI/CD, y separarlos es lo que permite desplegar sin
miedo.

| | Release | Despliegue |
|---|---|---|
| Qué es | Un artefacto inmutable con una versión | Poner ese artefacto en un entorno |
| Dónde vive | Una etiqueta `v1.2.0` en Git | En ningún sitio de Git |
| Cuántas veces ocurre | Se construye **una vez** | Se despliega **muchas** |
| En GitHub | Un *Release* | Un *Environment* con sus reglas |

**La regla que hay que interiorizar: nunca se reconstruye para producción.** Se
promociona la imagen exacta que pasó las pruebas, identificada por su *digest*. Si se
reconstruye, se está desplegando algo que nadie ha probado: aunque el commit sea el
mismo, el resultado puede no serlo (una dependencia transitiva que cambió, una imagen
base actualizada, un reloj distinto).

## 2. El modelo de ramas

**Tronco principal con ramas cortas.** No GitFlow: aquello se diseñó para software con
varias versiones vivas en manos de clientes, y sus ramas largas divergen y se
reintegran con dolor cuando lo que haces es desplegar de continuo.

```
feature/lo-que-sea ──► pull request ──► CI en verde ──► fusión a main
                                                            │
                                                            ▼
                                            imagen sha-abc1234 en ECR
                                                            │
                                                            ▼
                                                  despliegue a DEV
                                                            │
                                       etiqueta v1.2.0 ──────┘
                                                            │
                                                            ▼
                                    (con PROD: aprobación humana y a producción)
```

`main` siempre debe ser desplegable. Una rama corta, un *pull request*, CI en verde
como condición para fusionar.

---

## 3. Fase 1: integración continua

El workflow vive en [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) y se
dispara en cada *pull request* y en cada fusión a `main`.

### Los carriles

La división **no es por lo que prueban, sino por lo que necesitan**. Es la misma que
explica la sección 3 de [`TESTING.md`](TESTING.md), y aquí se traduce en tres trabajos
que corren en paralelo:

| Trabajo | Proyectos | Necesita | Casos |
|---|---:|---|---:|
| `unitarias` | 9 | Nada | 273 |
| `integracion` | 4 | Docker (Testcontainers) | 71 |
| `extremo-a-extremo` | Humo + Playwright | La pila con compose | 37 |

Que dos tercios de la suite no necesiten nada es lo que permite que el carril rápido
responda en un par de minutos. Es el retorno directo de la arquitectura hexagonal, y en
CI se nota más que en local.

### La guarda contra el olvido

Los carriles son listas explícitas en el bloque `env` del workflow. Eso tiene una
ventaja —cada proyecto nuevo obliga a decidir conscientemente dónde encaja— y un riesgo
obvio: que alguien añada un proyecto y se olvide de listarlo, con lo que sus pruebas
**no se ejecutarían nunca en CI y nadie se daría cuenta**.

Por eso hay un cuarto trabajo, `cobertura-de-carriles`, que recorre `tests/` y falla si
encuentra un proyecto que no esté en ninguna lista:

```bash
case "$asignados" in
  *" $nombre "*) echo "  ok   $nombre" ;;
  *) echo "::error::$nombre no esta asignado a ningun carril de CI"; faltan=1 ;;
esac
```

Es barato y cubre el único fallo que este diseño puede tener en silencio.

### Decisiones concretas

**`concurrency` con `cancel-in-progress`.** Si llegan dos empujones seguidos a la misma
rama, se cancela el anterior. Nadie necesita el resultado de un commit ya reemplazado, y
en un repositorio con E2E eso ahorra minutos de runner.

**`permissions: contents: read`.** Este workflow no publica nada. Cuando llegue el de
despliegue necesitará más, y se declarará allí y no antes: el permiso mínimo es un
valor por defecto, no una excepción.

**Caché de NuGet por contenido, no por rama.** No hay `packages.lock.json`, así que la
clave se calcula sobre los ficheros que determinan las dependencias:

```yaml
key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', 'Directory.Packages.props', 'Directory.Build.props') }}
```

Con `restore-keys` como respaldo, un cambio en un `.csproj` reaprovecha la caché
anterior en vez de descargarlo todo de cero.

**`--no-build` sobre una compilación en `Release`.** Cada carril compila una vez y las
pruebas reutilizan esa salida, en lugar de recompilar por proyecto.

**Diagnóstico incluido de serie.** El carril de E2E publica `docker compose ps` siempre,
y si algo falla sube el informe de Playwright como artefacto y vuelca los últimos 200
renglones de log de los diez contenedores. Un fallo de E2E en un runner ajeno es
imposible de depurar sin eso.

### Lo que todavía no hace

- No construye ni publica imágenes: eso es la fase 4.
- La caché de capas de Docker no está montada, así que el carril de E2E reconstruye las
  seis imágenes en cada ejecución. Es la optimización más rentable pendiente.
- No hay reglas de protección en `main`. Se configuran en GitHub, no en el repositorio,
  y el paso siguiente es exigir que los cuatro trabajos pasen antes de poder fusionar.

---

## 4. Qué cambiaría con un entorno de producción

Menos de lo que parece, y esa es la gracia de los *Environments* de GitHub. Un entorno
lleva sus propios secretos y variables, y sus reglas de protección:

| Entorno | Se despliega cuando | Protección |
|---|---|---|
| `dev` | En cada fusión a `main` | Ninguna: esa es exactamente su función |
| `prod` | Al publicar una etiqueta `v*` | Revisor obligatorio, y solo desde etiquetas |

Añadir `environment: prod` a un trabajo hace que GitHub lo **detenga y espere** a que
una persona apruebe. Es una línea en el workflow más la configuración del entorno; el
resto del *pipeline* no cambia.

Y como la imagen ya existe desde que se fusionó a `main`, el despliegue a producción no
compila nada: solo apunta el servicio al *digest* que ya está en el registro. Eso es lo
que hace que promocionar sea rápido y aburrido, que es como debe ser.
