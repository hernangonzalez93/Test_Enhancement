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

  # SIN bloque health_check_custom_config, y es deliberado.
  #
  # Ese bloque le diria a Cloud Map "no sondees tu, ya lo hace ECS", que es lo
  # correcto conceptualmente. El problema es practico: su unico argumento esta
  # deprecado, un bloque vacio NO se guarda en el estado, y el bloque obliga a
  # recrear el recurso. Resultado: cada plan queria destruir y recrear el
  # registro DNS de Kafka sin que nada hubiese cambiado.
  #
  # Omitirlo no cambia el comportamiento: ECS registra la tarea al arrancarla y
  # la da de baja al pararla igualmente, y un espacio de nombres privado no
  # admite sondas de Route 53, asi que Cloud Map tampoco iba a sondear nada.

  tags = { Name = "${var.project}-kafka" }
}

output "dns_interno" {
  description = "Nombre por el que los servicios se encuentran dentro de la VPC."
  value       = "kafka.${aws_service_discovery_private_dns_namespace.interno.name}:9092"
}

# ---------------------------------------------------------------------------
# Y un nombre para cada servicio de aplicacion
# ---------------------------------------------------------------------------
# No solo Kafka: Rentals llama a Fleet y a Pricing por HTTP, y necesita
# encontrarlos igual. Por el balanceador tambien podria, pero la llamada
# saldria a internet y volveria a entrar, pagando latencia y trafico para
# hablar con un vecino.
#
# El nombre se queda sin el sufijo "-api": `fleet.testenforce.local` se lee
# mejor que `fleet-api.testenforce.local`, y es lo que espera la configuracion.
# ---------------------------------------------------------------------------

resource "aws_service_discovery_service" "servicio" {
  for_each = var.services

  name        = replace(each.key, "-api", "")
  description = "Resuelve a las tareas de ${each.key} en marcha"

  dns_config {
    namespace_id = aws_service_discovery_private_dns_namespace.interno.id

    dns_records {
      type = "A"
      ttl  = 10
    }

    routing_policy = "MULTIVALUE"
  }

  tags = { Name = "${var.project}-${each.key}" }
}
