# Infraestructura en AWS

Cómo está montado el despliegue, y por qué cada decisión es la que es. Se construye
por fases junto a [`CICD.md`](CICD.md); este documento crece con ellas.

| Fase | Estado |
|---|---|
| 3. Red, registro y logs | **Hecha** — secciones 3 a 9 |
| 4. Primer despliegue con OIDC | Pendiente |
| 5. Base de datos y balanceador | Pendiente |

---

## 1. El contexto económico, que aquí manda

Esto es un entorno de aprendizaje financiado con créditos. No es una restricción menor:
**condiciona casi todas las decisiones técnicas** de este documento, y conviene tenerlo
presente antes de copiar nada de aquí a un entorno real.

Dos hechos que descubrimos por el camino y que merece la pena dejar escritos:

**Las cuentas de AWS creadas desde 2025 ya no tienen la capa gratuita clásica de 12
meses.** En su lugar hay créditos con una ventana de meses. Ni RDS, ni el balanceador,
ni EC2 son gratis en ese modelo; solo se mantiene la capa *Always Free*, que no cubre
ninguno de los tres.

**Crear una organización de AWS puede cerrar esos créditos.** Al activar IAM Identity
Center —que exige una organización— la cuenta pasa de *Free Plan* a *Paid Plan*, y los
créditos asociados al primero vencen. Es la razón por la que este proyecto usa un
usuario IAM con clave en lugar de SSO: menos elegante, pero preserva los créditos.

## 2. Acceso

Un usuario IAM dedicado, `testenforce-terraform`, con su propio perfil del CLI. No se
comparte con otros proyectos: si algún día hay que rotar su clave, no arrastra nada más.

El proveedor **no fija el perfil en el código**:

```hcl
provider "aws" {
  region = var.region
  # El perfil se toma de la variable de entorno AWS_PROFILE
}
```

Así el mismo código sirve desde un portátil y desde GitHub Actions, donde en la fase 4
las credenciales llegarán por OIDC y no habrá ningún perfil que fijar.

## 3. La red

Dos zonas de disponibilidad, porque tanto el balanceador como el grupo de subredes de
RDS lo exigen. Cuatro subredes: dos públicas para las tareas y dos privadas para la base
de datos.

**No hay NAT Gateway**, y es deliberado. Son unos 32 $/mes, el sobrecoste clásico de un
entorno de aprendizaje. La alternativa que usamos: las tareas viven en subredes públicas
con IP pública, protegidas por grupos de seguridad que no dejan entrar a nadie salvo al
balanceador. La base de datos vive en subredes privadas **sin ruta a internet**, que es
exactamente donde debe estar.

Es un compromiso consciente. En producción, las tareas irían en privadas con NAT o con
*VPC endpoints*.

## 4. Grupos de seguridad: se referencian entre sí

La regla que los ordena: **cada grupo permite entrada únicamente desde el grupo
anterior**, nunca desde un rango de direcciones.

```
internet  ──►  balanceador  ──►  servicios  ──►  base de datos
```

```hcl
resource "aws_vpc_security_group_ingress_rule" "bd_desde_servicios" {
  security_group_id            = aws_security_group.base_de_datos.id
  referenced_security_group_id = aws_security_group.servicios.id
  from_port                    = 5432
  to_port                      = 5432
  ip_protocol                  = "tcp"
}
```

`referenced_security_group_id` en lugar de un CIDR es lo importante. Las direcciones de
las tareas cambian en cada despliegue; la pertenencia al grupo, no. La regla sigue
siendo cierta sin mantenimiento.

**Un puerto por servicio** en el balanceador, replicando la forma del compose local
(5101 para Rentals, 5102 para Pricing). Sin dominio propio no hay enrutado por host, y
el enrutado por ruta solaparía los `/health` de los servicios entre sí, rompiendo las
pruebas de humo, que construyen cada URL como base más `/health`.

## 5. Registro de imágenes, con fecha de caducidad

Un repositorio por servicio, con `IMMUTABLE` en las etiquetas: una etiqueta publicada no
se puede sobrescribir. Eso es lo que hace que "desplegar la imagen `sha-abc1234`"
signifique siempre lo mismo, hoy y dentro de tres meses.

Y una política de ciclo de vida que **no es opcional**: el almacenamiento se paga a
0,10 $ por GB y mes, y las imágenes de .NET pesan unos 350 MB. Sin limpieza, cada
despliegue deja una imagen más para siempre.

| Regla | Qué hace |
|---|---|
| 1 | Descarta las imágenes sin etiqueta al día siguiente |
| 2 | Conserva solo las 10 últimas versiones |

## 6. Logs con retención desde el primer minuto

Los grupos de logs los crea Terraform, no ECS. Si se deja que ECS los cree solos,
**nacen sin retención y crecen indefinidamente**; CloudWatch cobra ese almacenamiento
para siempre. Es una de las facturas sorpresa más habituales de AWS y se evita con una
línea:

```hcl
retention_in_days = var.log_retention_days   # 7
```

Las migraciones tienen su propio grupo, separado del servicio: cuando una falla, quieres
sus logs aislados y no mezclados con el tráfico normal.

## 7. El estado, en S3

El estado vive en un bucket de S3, no en tu disco. La razón es que **GitHub Actions
tiene que poder leerlo y escribirlo**: con estado local, un workflow no ve nada de lo
aplicado y trataría de crearlo todo otra vez.

Lleva versionado, cifrado, acceso público bloqueado y `use_lockfile` para que dos
`apply` simultáneos no se pisen. El detalle completo está en
[`AWS-GITOPS.md`](AWS-GITOPS.md).

El nombre del bucket **no está en el código**: lleva dentro el número de cuenta y este
repositorio es público. Se pasa al inicializar:

```bash
terraform init -backend-config="bucket=<nombre>"
```

En cambio `.terraform.lock.hcl` **sí se versiona**: fija la versión exacta del proveedor
para que tu equipo y CI usen la misma.

## 8. Etiquetas y control del gasto

Todo recurso que las admita recibe estas etiquetas automáticamente:

```hcl
default_tags {
  tags = {
    Project     = "TestEnforce"
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}
```

Sin ellas, el gasto de este proyecto se mezcla con el resto de la cuenta y no hay forma
de separarlo. Con ellas se puede filtrar en Cost Explorer y crear un presupuesto propio.

### El presupuesto

Un presupuesto mensual con tres avisos, definido en
[`bootstrap/presupuesto.tf`](../infra/bootstrap/presupuesto.tf). Los dos primeros
presupuestos de una cuenta son gratis.

**Vive en el bootstrap y no en `infra/`, y la razón se aprendió fallando.** Estaba en
`infra/`, se lanzó el `destroy`, y la alarma de coste desapareció junto con lo que
vigilaba. Una red de seguridad no debe destruirse con aquello de lo que protege.

En el bootstrap sobrevive a cualquier ciclo de destruir y recrear, y cubre también lo que
se cree a mano en la consola mientras la pila está desmontada.

| Umbral | Tipo | Para qué |
|---|---|---|
| 50 % | Real | Enterarse de una tendencia, pronto |
| 80 % | Real | Aviso serio |
| 100 % | **Proyectado** | El único que avisa **antes** de llegar |

El proyectado es el más valioso: AWS estima el gasto del mes según el ritmo actual, así
que algo encendido por descuido un viernes salta el sábado y no el día 28.

**La línea que lo hace útil aquí:**

```hcl
cost_types {
  include_credit = false
}
```

Por defecto AWS **resta los créditos** del coste. Con créditos disponibles, el
presupuesto vería 0 $ y no avisaría nunca. Con esto vigila el gasto **bruto**: avisa de
lo que se consume mientras los créditos todavía lo cubren, en lugar de descubrirlo
cuando se agoten.

**La cifra refleja lo que se espera gastar, no lo que uno podría permitirse.** Un
presupuesto tan alto que nunca salta no detecta nada. Con la infraestructura actual en
0 $, cualquier gasto es una sorpresa que merece un aviso; cuando lleguen la base de
datos y el balanceador se sube deliberadamente.

El correo **nunca va en el código**: el repositorio es público y es un dato personal.
Como el bootstrap se aplica a mano, se pone en `infra/bootstrap/terraform.tfvars`, que
está en `.gitignore`. Hay un `terraform.tfvars.example` al lado con la forma.

## 9. Cómo se aplica

**No se aplica a mano.** Un *pull request* que toque `infra/` publica su plan como
comentario, y fusionar lo aplica. Todo el detalle en [`AWS-GITOPS.md`](AWS-GITOPS.md).

La excepción es `infra/bootstrap/`, que crea el bucket del estado y los roles de OIDC:
eso sí se aplica una vez desde tu equipo, porque es lo que hace posible todo lo demás.

### Aplicar y destruir por sesión

Con presupuesto limitado, la costumbre más rentable es levantar la infraestructura al
empezar a trabajar y destruirla al terminar:

```bash
terraform destroy
```

El gasto pasa a ser proporcional a las horas reales. Y tiene un beneficio que va más
allá del dinero: **obliga a que la infraestructura sea de verdad reproducible.** Si un
`destroy` seguido de un `apply` no devuelve el sistema funcionando, hay un problema que
conviene descubrir ahora y no dentro de seis meses.

Lo que esta fase crea —red, registro y grupos de logs— **no cuesta nada por existir**,
así que puede quedarse levantado sin problema. Lo que sí conviene destruir entre
sesiones son la base de datos y el balanceador, que llegan en la fase 5.

---

## 10. Descubrimiento de servicios

Hasta Kafka, todo se alcanzaba por el balanceador con puertos públicos. Kafka **no debe
ser público**, y las IP de las tareas cambian en cada despliegue, así que nadie puede
apuntar a una dirección fija.

**Cloud Map** crea un DNS privado dentro de la VPC. ECS registra la tarea al arrancarla y
la da de baja al pararla, y los demás servicios llaman a un nombre estable:

```
kafka.testenforce.local:9092
```

Es el equivalente al DNS interno que `docker compose` da gratis —ahí `kafka` resuelve al
contenedor sin que nadie lo configure—. En AWS hay que pedirlo explícitamente.

### La línea que lo hace funcionar

```hcl
KAFKA_ADVERTISED_LISTENERS = "PLAINTEXT://kafka.testenforce.local:9092"
```

Kafka tiene un comportamiento que sorprende: cuando un cliente se conecta, el broker le
responde *«para hablar conmigo, usa esta dirección»*. Si anunciara su IP privada, el
cliente la usaría **hasta que la tarea se reemplazase**, y entonces fallaría sin entender
por qué. El nombre del descubrimiento sobrevive a los reemplazos; la IP no.

El TTL del registro es de 10 segundos, para que los clientes dejen de usar una dirección
muerta enseguida.

## 11. El disco de Fargate, y por qué aquí se acepta perderlo

Una tarea de Fargate trae 20 GB de disco, **atados a la vida de la tarea**. Muere la
tarea, muere el disco. Y las tareas mueren a menudo:

| Cuándo | Cada cuánto |
|---|---|
| En cada despliegue | Al publicar versión |
| Si falla la sonda | ECS la mata y arranca otra |
| **Al apagar y encender** | **Cada noche, en este proyecto** |
| Mantenimiento de la plataforma | Cuando AWS quiere |

Kafka guarda en disco los mensajes, los marcadores de posición de cada consumidor, y la
identidad del clúster. Perderlo significa que los mensajes no consumidos desaparecen, los
consumidores pierden su sitio, y el clúster se considera uno nuevo.

**Aquí es aceptable** porque Kafka es **transporte y no almacén**: una renta vive en la
base de datos de Rentals, un vehículo bloqueado en la de Fleet. Y como todo se apaga a la
vez, no quedan mensajes en vuelo ni consumidores con un marcador obsoleto.

**En producción no lo sería**, y por un motivo distinto: allí las tareas se reemplazan de
una en una mientras el sistema atiende tráfico, así que un mensaje publicado y no
consumido en ese instante se perdería sin que nadie lo notase. Ahí haría falta montar
EFS.

---

## 12. La base de datos

**Una sola instancia para los tres servicios** que tienen base de datos. Es lo mismo que
hace el compose: una base llamada `testenforce` con tres esquemas dentro -`rentals`,
`fleet` y `billing`-. Tres instancias separadas serian 45 $/mes en lugar de 15, y no
ensenarian nada distinto.

Vive en las **subredes privadas**, las que se crearon en la fase 3 y hasta ahora no
usaba nadie: existian esperando precisamente esto. No tienen ruta a internet, asi que la
base de datos no puede salir ni ser alcanzada desde fuera; solo desde el grupo de
seguridad de las tareas.

### La contrasena no la escribe nadie

La genera Terraform con `random_password` y la guarda en Parameter Store como
`SecureString`. Ni tu ni yo la vemos nunca.

Hay un parametro por servicio, **con el nombre exacto de la variable de entorno** que
espera cada aplicacion:

```
/testenforce/dev/ConnectionStrings__RentalsDatabase
```

Asi la definicion de tarea solo tiene que apuntar al parametro: no hay que traducir nada
entre lo que guarda AWS y lo que lee .NET.

Y sin caracteres especiales a proposito: acaban dentro de una cadena de conexion, donde
un `;` o un `=` la partirian por la mitad.

### Quien lee los secretos

El **rol de ejecucion**, no el de tarea. Los resuelve el agente de ECS antes de arrancar
el contenedor, asi que el permiso va ahi. Es el error mas repetido al configurar secretos
en ECS.

Y hacen falta dos permisos, no uno: `ssm:GetParameters` para leerlo y `kms:Decrypt` para
descifrarlo. Con solo el primero, la tarea falla al arrancar con un error que **no
menciona KMS por ningun lado**.

### Los dos ajustes que hacen que el `destroy` funcione

```hcl
skip_final_snapshot = true
deletion_protection = false
```

Sin el primero, destruir pide un nombre de copia final y aborta. Sin el segundo, RDS se
niega directamente. Es la misma trampa que el `force_delete` de ECR, con la misma
consecuencia: una destruccion a medias.

Son aceptables porque esta base se reconstruye desde cero -las migraciones la recrean y
los datos son de prueba-. Con datos reales, los dos deberian estar al reves.

### Apagarla no es lo mismo que apagar una tarea

Un servicio de ECS se apaga poniendolo a cero tareas. Una instancia de RDS no tiene ese
concepto: **se para**, y es una llamada distinta. El apagado nocturno y el interruptor de
nivel 1 hacen las dos cosas.

Dos advertencias:

**Parada no es gratis.** Se deja de pagar el computo -lo caro- pero se sigue pagando el
almacenamiento, unos 2 $/mes por los 20 GB. Para llegar a cero de verdad hace falta el
nivel 2.

**AWS la vuelve a encender sola a los 7 dias.** Es una politica suya, para poder aplicar
mantenimiento. Si se va a estar mas de una semana sin tocarla, es mejor destruirla que
dejarla parada.
