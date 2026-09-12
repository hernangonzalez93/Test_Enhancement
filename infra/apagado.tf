# ---------------------------------------------------------------------------
# Apagado nocturno automatico
# ---------------------------------------------------------------------------
# Una red de seguridad contra el olvido: cada noche, todos los servicios bajan
# a cero tareas. Deliberadamente NO existe un encendido automatico por la
# manyana, porque gastar dinero deberia requerir una accion consciente y
# olvidarse de apagar es mucho mas facil que olvidarse de encender.
#
# El apagado no destruye nada: el cluster, los roles y la definicion de tarea
# siguen ahi. Solo deja de haber contenedores en marcha, que es lo unico que
# se factura por horas.
# ---------------------------------------------------------------------------

variable "apagado_cron" {
  description = <<-EOT
    Cuando se apaga todo, en formato cron de EventBridge. Por defecto a las
    22:00 hora peninsular espanyola.

    El formato lleva seis campos: minuto, hora, dia del mes, mes, dia de la
    semana y anyo. El interrogante significa "cualquiera" en los campos de dia,
    y es obligatorio poner uno de los dos dias como interrogante.
  EOT
  type        = string
  default     = "cron(0 22 * * ? *)"
}

variable "apagado_zona_horaria" {
  description = "Zona horaria del apagado. Con esto el horario de verano se ajusta solo."
  type        = string
  default     = "Europe/Madrid"
}

# ---------------------------------------------------------------------------
# El permiso para apagar, acotado a lo minimo
# ---------------------------------------------------------------------------

data "aws_iam_policy_document" "confianza_planificador" {
  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["scheduler.amazonaws.com"]
    }

    # Sin esta condicion, cualquier planificador de la cuenta podria asumir el
    # rol. Con ella, solo los de esta cuenta.
    condition {
      test     = "StringEquals"
      variable = "aws:SourceAccount"
      values   = [data.aws_caller_identity.actual.account_id]
    }
  }
}

resource "aws_iam_role" "planificador" {
  name               = "${var.project}-apagado"
  description        = "Permite al planificador poner los servicios a cero tareas"
  assume_role_policy = data.aws_iam_policy_document.confianza_planificador.json
}

data "aws_iam_policy_document" "apagar" {
  statement {
    effect  = "Allow"
    actions = ["ecs:UpdateService"]

    # Solo los servicios de ESTE cluster, y nada mas. Un rol que solo sabe
    # hacer una cosa sobre un sitio concreto.
    resources = ["arn:aws:ecs:${var.region}:${data.aws_caller_identity.actual.account_id}:service/${aws_ecs_cluster.principal.name}/*"]
  }

  # Parar la base de datos, y solo esta. Una instancia de RDS no tiene
  # "cero tareas": o esta encendida o esta parada, y pararla es una llamada
  # distinta de bajar un servicio de ECS.
  statement {
    effect    = "Allow"
    actions   = ["rds:StopDBInstance"]
    resources = [aws_db_instance.principal.arn]
  }
}

resource "aws_iam_role_policy" "apagar" {
  name   = "apagar-servicios"
  role   = aws_iam_role.planificador.id
  policy = data.aws_iam_policy_document.apagar.json
}

# ---------------------------------------------------------------------------
# La cita nocturna, una por servicio
# ---------------------------------------------------------------------------

resource "aws_scheduler_schedule" "apagado" {
  # TODOS los servicios de ECS, sin enumerarlos. Antes habia una lista a mano,
  # y una lista a mano se olvida: un servicio nuevo se habria quedado encendido
  # toda la noche sin que nadie lo notase hasta ver la factura.
  for_each = merge(
    { for nombre, s in aws_ecs_service.servicio : nombre => s.name },
    { kafka = aws_ecs_service.kafka.name }
  )

  name                         = "${var.project}-apagar-${each.key}"
  description                  = "Baja ${each.value} a cero tareas cada noche"
  schedule_expression          = var.apagado_cron
  schedule_expression_timezone = var.apagado_zona_horaria

  flexible_time_window {
    mode = "OFF"
  }

  target {
    # Un "destino universal": el planificador llama directamente a la API de
    # AWS, sin necesidad de una funcion Lambda intermedia.
    arn      = "arn:aws:scheduler:::aws-sdk:ecs:updateService"
    role_arn = aws_iam_role.planificador.arn

    input = jsonencode({
      Cluster      = aws_ecs_cluster.principal.name
      Service      = each.value
      DesiredCount = 0
    })
  }
}

output "apagado_nocturno" {
  description = "Cuando se apagan los servicios automaticamente."
  value       = "${var.apagado_cron} (${var.apagado_zona_horaria})"
}

# ---------------------------------------------------------------------------
# Y la base de datos, que se para de otra manera
# ---------------------------------------------------------------------------
# Un servicio de ECS se apaga poniendolo a cero tareas. Una instancia de RDS no
# tiene ese concepto: se para, y es una llamada distinta.
#
# DOS ADVERTENCIAS que conviene tener presentes:
#
#   1. Parada NO es gratis. Se deja de pagar el computo —lo caro— pero se sigue
#      pagando el almacenamiento, unos 2 $/mes por los 20 GB. Para dejarlo a
#      cero de verdad hace falta el nivel 2.
#
#   2. AWS la vuelve a encender sola a los 7 dias. Es una politica suya, para
#      poder aplicar mantenimiento. Si se va a estar mas de una semana sin
#      tocarla, mejor destruirla que dejarla parada.
# ---------------------------------------------------------------------------

resource "aws_scheduler_schedule" "apagar_base_de_datos" {
  name                         = "${var.project}-apagar-bd"
  description                  = "Para la base de datos cada noche"
  schedule_expression          = var.apagado_cron
  schedule_expression_timezone = var.apagado_zona_horaria

  flexible_time_window {
    mode = "OFF"
  }

  target {
    arn      = "arn:aws:scheduler:::aws-sdk:rds:stopDBInstance"
    role_arn = aws_iam_role.planificador.arn

    input = jsonencode({
      DbInstanceIdentifier = aws_db_instance.principal.identifier
    })
  }
}
