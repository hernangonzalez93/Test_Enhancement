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

## 7. El estado, y su riesgo

Por ahora el estado es **local**. Es lo más simple y no cuesta nada, pero tiene un
peligro real: si pierdes `terraform.tfstate`, Terraform deja de saber qué recursos
existen, y te quedan huérfanos en AWS **cobrando sin que nada los gestione**.

Está en `.gitignore` porque contiene identificadores y valores sensibles. Cuando
convenga, se migra a S3 sin rehacer nada:

```bash
terraform init -migrate-state
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

Un detalle que se descubre tarde: **los créditos hacen que el gasto real sea 0 $, pero
los presupuestos avisan sobre el gasto *antes* de aplicar créditos.** Eso es justo lo
que quieres: enterarte del consumo mientras los créditos aún cubren, y no cuando se
hayan agotado.

## 9. Cómo se aplica

```bash
export AWS_PROFILE=testenforce-b
cd infra
terraform init
terraform plan -out=plan.tfplan
terraform apply plan.tfplan
```

Guardar el plan en un fichero y aplicar **ese fichero** —en vez de `terraform apply` a
secas— garantiza que se ejecuta exactamente lo que revisaste, y no lo que la
infraestructura haya pasado a ser entre medias.

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
