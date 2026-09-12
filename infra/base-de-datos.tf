# ---------------------------------------------------------------------------
# PostgreSQL gestionado
# ---------------------------------------------------------------------------
# UNA sola instancia para los tres servicios que tienen base de datos, igual
# que en el compose: una base llamada `testenforce` y tres esquemas dentro
# —rentals, fleet y billing—. Tres instancias separadas serian 45 $/mes en
# lugar de 15, y no ensenyarian nada que esta no ensenye.
#
# Es el primer recurso del proyecto que guarda datos de verdad. Todo lo demas
# —tareas, imagenes, el propio Kafka— se puede destruir y recrear sin perder
# nada. Esto no.
# ---------------------------------------------------------------------------

resource "aws_db_subnet_group" "principal" {
  name = var.project
  # Las subredes privadas, las que no tienen ruta a internet. Se crearon en la
  # fase 3 y hasta ahora no las usaba nadie: existian esperando precisamente
  # esto.
  subnet_ids = aws_subnet.privada[*].id

  tags = { Name = "${var.project}-bd" }
}

# La contrasenya la genera Terraform y no la escribe ni la ve nadie. Sin
# caracteres especiales a proposito: acaban dentro de una cadena de conexion,
# y un `;` o un `=` la partirian por la mitad.
resource "random_password" "bd" {
  length  = 32
  special = false
}

resource "aws_db_instance" "principal" {
  identifier = var.project

  engine         = "postgres"
  engine_version = "17"
  # ARM de AWS: mismo rendimiento que el equivalente Intel por menos dinero.
  instance_class = "db.t4g.micro"

  db_name  = var.project
  username = var.project
  password = random_password.bd.result

  # 20 GB es el minimo de gp3, y sobra de largo para lo que hay aqui.
  allocated_storage = 20
  storage_type      = "gp3"
  storage_encrypted = true

  db_subnet_group_name   = aws_db_subnet_group.principal.name
  vpc_security_group_ids = [aws_security_group.base_de_datos.id]
  # Sin direccion publica. Solo se alcanza desde dentro de la VPC, y ademas
  # solo desde el grupo de seguridad de las tareas.
  publicly_accessible = false

  # Una sola zona: la alta disponibilidad duplica el precio y aqui no aporta.
  multi_az = false

  # Un dia de copias. Es casi gratis —se cobra el exceso sobre el tamanyo de la
  # base— y deja la puerta abierta a recuperar un punto en el tiempo.
  backup_retention_period = 1
  backup_window           = "02:00-03:00"
  maintenance_window      = "mon:03:30-mon:04:30"

  # ---- Los dos que hacen que el destroy funcione ----
  # Sin el primero, destruir pide un nombre de copia final y aborta. Sin el
  # segundo, RDS se niega directamente. Es el mismo tipo de trampa que el
  # force_delete de ECR, y con la misma consecuencia: una destruccion a medias.
  #
  # Son aceptables porque esta base se puede recrear desde cero: las
  # migraciones la reconstruyen y los datos son de prueba. En una con datos
  # reales, los dos deberian estar al reves.
  skip_final_snapshot = true
  deletion_protection = false

  # Los logs de PostgreSQL a CloudWatch, donde ya esta todo lo demas.
  enabled_cloudwatch_logs_exports = ["postgresql"]

  # La actualizan sola dentro de la misma version mayor.
  auto_minor_version_upgrade = true

  tags = { Name = var.project }
}

# ---------------------------------------------------------------------------
# La cadena de conexion, en Parameter Store
# ---------------------------------------------------------------------------
# Un parametro por servicio, con el nombre EXACTO de la variable de entorno que
# espera cada aplicacion. Asi la definicion de tarea solo tiene que apuntar al
# parametro: no hay que traducir nada.
#
# SecureString: se cifra en reposo. El nivel estandar de Parameter Store es
# gratis, a diferencia de Secrets Manager.
# ---------------------------------------------------------------------------

resource "aws_ssm_parameter" "cadena_conexion" {
  for_each = toset(["Rentals", "Fleet", "Billing"])

  name        = "/${var.project}/${var.environment}/ConnectionStrings__${each.key}Database"
  description = "Cadena de conexion de ${each.key}"
  type        = "SecureString"

  value = join(";", [
    "Host=${aws_db_instance.principal.address}",
    "Port=${aws_db_instance.principal.port}",
    "Database=${aws_db_instance.principal.db_name}",
    "Username=${aws_db_instance.principal.username}",
    "Password=${random_password.bd.result}",
  ])

  tags = { Name = "${var.project}-${each.key}" }
}

# El rol de EJECUCION es quien lee los parametros, no el de tarea: los resuelve
# el agente de ECS antes de arrancar el contenedor. Es el error mas repetido al
# configurar secretos en ECS.
data "aws_iam_policy_document" "leer_parametros" {
  statement {
    effect    = "Allow"
    actions   = ["ssm:GetParameters"]
    resources = [for p in aws_ssm_parameter.cadena_conexion : p.arn]
  }

  # Un SecureString se cifra con KMS, asi que ademas de leerlo hay que poder
  # descifrarlo. Sin esto, la tarea falla al arrancar con un error que no
  # menciona KMS por ningun lado.
  statement {
    effect    = "Allow"
    actions   = ["kms:Decrypt"]
    resources = ["arn:aws:kms:${var.region}:${data.aws_caller_identity.actual.account_id}:key/alias/aws/ssm"]
  }
}

resource "aws_iam_role_policy" "leer_parametros" {
  name   = "leer-cadenas-de-conexion"
  role   = aws_iam_role.ejecucion.id
  policy = data.aws_iam_policy_document.leer_parametros.json
}

output "base_de_datos" {
  description = "Donde escucha la base de datos, dentro de la VPC."
  value       = "${aws_db_instance.principal.address}:${aws_db_instance.principal.port}"
}
