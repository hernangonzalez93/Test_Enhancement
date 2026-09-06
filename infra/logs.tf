# ---------------------------------------------------------------------------
# Grupos de logs, creados por Terraform y no por ECS.
#
# Si se deja que ECS los cree solos, nacen SIN retencion y crecen para siempre;
# CloudWatch cobra por almacenamiento indefinidamente. Es una de las facturas
# sorpresa mas comunes de AWS, y se evita con una linea.
# ---------------------------------------------------------------------------

resource "aws_cloudwatch_log_group" "servicio" {
  for_each = toset(var.services)

  name              = "/ecs/${var.project}/${each.key}"
  retention_in_days = var.log_retention_days

  tags = { Name = "${var.project}-${each.key}" }
}

# Las migraciones corren como tarea puntual y tienen su propio grupo: cuando
# una falla, quieres sus logs aislados y no mezclados con los del servicio.
resource "aws_cloudwatch_log_group" "migraciones" {
  name              = "/ecs/${var.project}/migraciones"
  retention_in_days = var.log_retention_days

  tags = { Name = "${var.project}-migraciones" }
}
