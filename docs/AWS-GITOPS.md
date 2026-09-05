# Infraestructura desde GitHub Actions

Cómo se aplica la infraestructura sola al fusionar un *pull request*, y por qué está
montado así.

---

## 1. El modelo

```
rama ──► pull request ──► terraform plan ──► comentario en el PR
                                                    │
                                          revisas el plan, no el código
                                                    │
                                                fusionar
                                                    │
                                                    ▼
                                          terraform apply (entorno dev)
```

La idea de fondo: **la revisión ocurre sobre el plan, no sobre el código.** Leer un
`.tf` y adivinar qué hará es mucho más difícil que leer «voy a crear 25 recursos, estos».
Por eso el plan se publica como comentario en el *pull request*, y por eso fusionar
significa aplicar.

## 2. Dos roles, y por qué no uno

Es la decisión de seguridad más importante de este montaje.

| Rol | Permisos | Quién puede asumirlo |
|---|---|---|
| `testenforce-github-plan` | `ReadOnlyAccess` | Cualquier referencia del repositorio |
| `testenforce-github-apply` | `AdministratorAccess` | **Solo** el entorno `dev` |

El motivo: **un plan se ejecuta sobre código que todavía nadie ha revisado.** Si hubiera
un único rol con permisos de escritura, cualquiera que abriese un *pull request*
modificando el propio workflow podría tocar la infraestructura antes de que nadie mirase
el cambio.

Con dos roles, un *pull request* como mucho puede leer.

La condición de confianza del rol de escritura es literalmente esto:

```hcl
condition {
  test     = "StringEquals"
  variable = "token.actions.githubusercontent.com:sub"
  values   = ["repo:hernangonzalez93/Test_Enhancement:environment:dev"]
}
```

Y en el workflow:

```yaml
environment: dev
```

Esa línea **no es decorativa**. Es lo que hace que GitHub emita un token cuyo `sub`
incluye `environment:dev`, que es la única forma de que AWS entregue el rol de
escritura. Un trabajo que la omita no puede aplicar nada, aunque corra en el mismo
repositorio y esté escrito por la misma persona.

Es también donde se añadirían revisores obligatorios el día que exista `prod`.

## 3. Sin claves de larga duración

GitHub Actions no guarda ninguna credencial de AWS. Presenta un token firmado por
GitHub, AWS comprueba que el emisor es de fiar y que el `sub` cumple la condición, y
entrega credenciales temporales que caducan solas.

Lo que sí hay son tres **variables** de repositorio, que no son secretos: un ARN de rol
y un nombre de bucket no sirven de nada sin la confianza configurada del lado de AWS.

| Variable | Qué es |
|---|---|
| `AWS_ROLE_PLAN` | ARN del rol de solo lectura |
| `AWS_ROLE_APPLY` | ARN del rol de escritura |
| `TF_STATE_BUCKET` | Nombre del bucket del estado |

El nombre del bucket va en una variable y no en el código porque **lleva dentro el
número de cuenta**, y este repositorio es público.

## 4. El estado, ahora en S3

Con estado local, un workflow no ve nada de lo aplicado: para él la infraestructura no
existe y trataría de crearla otra vez. Así que el estado pasa a S3, con:

- **Versionado.** Si un `apply` corrompe el estado, se vuelve a la versión anterior. Es
  la red de seguridad que por sí sola justifica S3.
- **Cifrado** en reposo y acceso público bloqueado.
- **`use_lockfile = true`.** Impide que dos `apply` simultáneos se pisen, sin necesidad
  de una tabla de DynamoDB aparte. Disponible desde Terraform 1.10.
- **Caducidad de versiones antiguas** a los 30 días, para que no se acumulen.

El bucket lleva `prevent_destroy`: el estado es lo único que no se puede reconstruir, y
perderlo deja todos los recursos huérfanos en AWS, cobrando sin que nada los gestione.

## 5. El huevo y la gallina

El bucket del estado no puede vivir dentro del estado, y el rol que Actions asume tiene
que existir antes de que Actions pueda asumir nada.

Por eso hay `infra/bootstrap/`: una configuración pequeña, **con estado local a
propósito**, que se aplica **una sola vez desde tu equipo** y después casi nunca se
toca. Crea el bucket, el proveedor de OIDC y los dos roles.

Es el único sitio del proyecto donde el estado local es la respuesta correcta y no un
atajo.

## 6. Detalles que importan

**`-lock=false` en el plan.** El rol de lectura no puede escribir el fichero de bloqueo.
Es seguro: un plan no modifica nada.

**Sin `cancel-in-progress` en el apply.** Interrumpir un `apply` a medias deja el estado
inconsistente. Si llegan dos fusiones seguidas, la segunda espera.

**Se vuelve a planificar antes de aplicar**, en lugar de reutilizar el plan del *pull
request*. Entre la revisión y la fusión pueden haber entrado otros cambios, y un plan
guardado se aplicaría sobre un estado que ya no es el mismo. Terraform lo rechazaría, y
con razón.

**Filtro por `paths`.** Los dos workflows solo se disparan si cambia `infra/**` o ellos
mismos. Un cambio en el código de los servicios no tiene por qué mover infraestructura.

## 7. Lo que este modelo implica

Conviene decirlo claro: **fusionar a `main` cambia infraestructura real.** No hay un
paso manual de confirmación después.

Las defensas son tres, y hay que entenderlas como un conjunto:

1. El plan está publicado en el *pull request* antes de fusionar
2. El rol de escritura solo lo alcanza el entorno `dev`
3. La regla de protección de `main` exige *pull request* y CI en verde

Si algún día esto apunta a producción, la cuarta defensa es un revisor obligatorio en el
entorno, que hace que el trabajo se detenga y espere a una persona.

## 8. Puesta en marcha

Una sola vez, desde tu equipo:

```bash
export AWS_PROFILE=testenforce-b
cd infra/bootstrap
terraform init
terraform plan -out plan.tfplan
terraform apply plan.tfplan
terraform output
```

Las tres salidas se copian a **Settings → Secrets and variables → Actions →
Variables** con los nombres de la tabla de la sección 3.

Después hay que crear el entorno en **Settings → Environments → New environment →
`dev`**. Sin él, el trabajo de apply falla al asumir el rol, porque el `sub` del token
no incluiría `environment:dev`.

A partir de ahí, ningún `terraform apply` vuelve a ejecutarse a mano.
