# ---------------------------------------------------------------------------
# Descubrimiento de servicios
# ---------------------------------------------------------------------------
# El problema que resuelve: hasta ahora todo se alcanzaba por el balanceador,
# con puertos publicos. Kafka NO debe ser publico, y las IP de las tareas
# cambian en cada despliegue, asi que nadie puede apuntar a una direccion fija.
#
# Cloud Map crea un DNS privado dentro de la VPC. Cuando ECS arranca una tarea
# la registra sola, y cuando la para la da de baja. Los demas servicios llaman
# a `kafka.testenforce.local` y siempre resuelve a la tarea viva.
#
# Es el equivalente en AWS al DNS interno que docker compose te da gratis: en
# el compose, `kafka` resuelve al contenedor de Kafka sin que nadie lo
# configure. Aqui hay que pedirlo explicitamente.
# ---------------------------------------------------------------------------

resource "aws_service_discovery_private_dns_namespace" "interno" {
  name        = "${var.project}.local"
  description = "DNS privado para que los servicios se encuentren entre si"
  vpc         = aws_vpc.principal.id

  tags = { Name = "${var.project}-interno" }
}

resource "aws_service_discovery_service" "kafka" {
  name        = "kafka"
  description = "Resuelve a la tarea de Kafka en marcha"

  dns_config {
    namespace_id = aws_service_discovery_private_dns_namespace.interno.id

    dns_records {
      type = "A"
      # TTL corto: cuando la tarea se reemplaza, los clientes tienen que
      # dejar de usar la IP vieja rapido. Diez segundos es un compromiso
      # razonable entre reaccion y trafico de consultas.
      ttl = 10
    }

    routing_policy = "MULTIVALUE"
  }

  # Deja que sea ECS quien confirme si la instancia esta sana, en vez de que
  # Cloud Map sondee por su cuenta. Es lo correcto aqui: ECS ya sabe si la
  # tarea vive, y una sonda propia seria una segunda fuente de verdad que
  # podria contradecirle.
  #
  # El bloque va vacio a proposito: su unico argumento, failure_threshold,
  # esta deprecado porque AWS lo fija siempre a 1.
  health_check_custom_config {}

  tags = { Name = "${var.project}-kafka" }
}

output "dns_interno" {
  description = "Nombre por el que los servicios se encuentran dentro de la VPC."
  value       = "kafka.${aws_service_discovery_private_dns_namespace.interno.name}:9092"
}
