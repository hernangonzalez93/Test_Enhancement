# ---------------------------------------------------------------------------
# Kafka como una tarea mas del cluster
# ---------------------------------------------------------------------------
# En lugar de MSK, que cuesta entre 130 y 550 $/mes segun la modalidad. Aqui el
# objetivo es aprender el pipeline y ver la mensajeria funcionando, no operar
# un broker gestionado. El codigo y las 411 pruebas no notan la diferencia:
# para ellos Kafka es una direccion en una variable de entorno.
#
# EL DISCO ES EFIMERO, y es una decision consciente. Una tarea de Fargate
# pierde su disco al morir, y las tareas mueren a menudo: en cada despliegue,
# si falla la sonda, y —en este proyecto— TODAS LAS NOCHES con el apagado
# automatico.
#
# Que significa perderlo:
#   - los mensajes publicados y aun no consumidos desaparecen
#   - los consumidores pierden su marcador de posicion
#   - el cluster se considera uno nuevo la proxima vez
#
# Es aceptable aqui porque Kafka es TRANSPORTE y no almacen: una renta vive en
# la base de datos de Rentals, un vehiculo bloqueado en la de Fleet. Y como
# todo se apaga a la vez, no quedan mensajes en vuelo ni consumidores con un
# marcador obsoleto.
#
# En produccion NO seria aceptable, y por un motivo distinto: alli las tareas
# se reemplazan de una en una mientras el sistema atiende trafico, asi que un
# mensaje publicado y no consumido en ese instante se perderia sin que nadie lo
# notase. Ahi es donde haria falta montar EFS.
# ---------------------------------------------------------------------------

resource "aws_security_group" "kafka" {
  name        = "${var.project}-kafka"
  description = "Broker de Kafka, alcanzable solo desde las tareas"
  vpc_id      = aws_vpc.principal.id

  tags = { Name = "${var.project}-kafka" }
}

# Ni una regla desde internet. Kafka solo habla con los servicios de dentro.
resource "aws_vpc_security_group_ingress_rule" "kafka_desde_servicios" {
  security_group_id            = aws_security_group.kafka.id
  description                  = "Kafka desde las tareas"
  referenced_security_group_id = aws_security_group.servicios.id
  from_port                    = 9092
  to_port                      = 9092
  ip_protocol                  = "tcp"
}

resource "aws_vpc_security_group_egress_rule" "kafka_salida" {
  security_group_id = aws_security_group.kafka.id
  description       = "Descargar la imagen y escribir logs"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "-1"
}

resource "aws_cloudwatch_log_group" "kafka" {
  name              = "/ecs/${var.project}/kafka"
  retention_in_days = var.log_retention_days

  tags = { Name = "${var.project}-kafka" }
}

locals {
  # El nombre por el que el resto del sistema encuentra al broker. Es lo que
  # Kafka tiene que ANUNCIAR: un cliente se conecta, Kafka le responde "para
  # hablar conmigo usa esta direccion", y si anunciara su IP privada, el
  # cliente la usaria hasta que la tarea se reemplazase.
  kafka_host = "kafka.${aws_service_discovery_private_dns_namespace.interno.name}"
}

resource "aws_ecs_task_definition" "kafka" {
  family                   = "${var.project}-kafka"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.ejecucion.arn
  task_role_arn            = aws_iam_role.tarea.arn

  container_definitions = jsonencode([
    {
      name  = "kafka"
      image = "confluentinc/cp-kafka:7.7.1"

      portMappings = [{ containerPort = 9092, protocol = "tcp" }]

      environment = [
        # Modo KRaft: un solo proceso hace de broker y de controlador, sin
        # ZooKeeper. Para un nodo unico es lo mas simple que existe.
        { name = "KAFKA_NODE_ID", value = "1" },
        { name = "KAFKA_PROCESS_ROLES", value = "broker,controller" },
        { name = "CLUSTER_ID", value = "MkU3OEVBNTcwNTJENDM2Qk" },

        # El controlador solo habla consigo mismo: es el unico nodo.
        { name = "KAFKA_CONTROLLER_QUORUM_VOTERS", value = "1@localhost:29093" },
        { name = "KAFKA_CONTROLLER_LISTENER_NAMES", value = "CONTROLLER" },

        # Escucha en todas las interfaces, porque la IP de la tarea no se
        # conoce de antemano.
        { name = "KAFKA_LISTENERS", value = "PLAINTEXT://0.0.0.0:9092,CONTROLLER://0.0.0.0:29093" },

        # LA LINEA CLAVE. Lo que Kafka le dice a los clientes para que vuelvan
        # a encontrarlo. Tiene que ser el nombre del descubrimiento de
        # servicios y no la IP: la IP muere con la tarea, el nombre no.
        { name = "KAFKA_ADVERTISED_LISTENERS", value = "PLAINTEXT://${local.kafka_host}:9092" },

        { name = "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", value = "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT" },
        { name = "KAFKA_INTER_BROKER_LISTENER_NAME", value = "PLAINTEXT" },

        # Un solo nodo: no hay donde replicar.
        { name = "KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", value = "1" },
        { name = "KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", value = "1" },
        { name = "KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", value = "1" },
        { name = "KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS", value = "0" },
        { name = "KAFKA_AUTO_CREATE_TOPICS_ENABLE", value = "true" },

        # La JVM se queda corta de memoria si no se le pone limite: intentaria
        # usar una fraccion del total y chocaria con el limite de la tarea.
        { name = "KAFKA_HEAP_OPTS", value = "-Xmx512M -Xms512M" }
      ]

      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.kafka.name
          "awslogs-region"        = var.region
          "awslogs-stream-prefix" = "ecs"
        }
      }

      # Una comprobacion de puerto TCP con bash, sin arrancar una JVM cada
      # treinta segundos: las herramientas de Kafka tardan varios segundos solo
      # en levantar el proceso.
      healthCheck = {
        command  = ["CMD-SHELL", "bash -c 'echo > /dev/tcp/localhost/9092' || exit 1"]
        interval = 30
        timeout  = 5
        retries  = 3
        # Kafka tarda en arrancar. Con menos margen, ECS lo mataria antes de
        # que llegue a estar listo y entraria en un bucle de reinicios.
        startPeriod = 120
      }
    }
  ])

  tags = { Name = "${var.project}-kafka" }
}

resource "aws_ecs_service" "kafka" {
  name            = "kafka"
  cluster         = aws_ecs_cluster.principal.id
  task_definition = aws_ecs_task_definition.kafka.arn
  launch_type     = "FARGATE"

  # Igual que el resto: nace apagado y no cuesta nada hasta que se enciende.
  desired_count = 0

  network_configuration {
    subnets          = aws_subnet.publica[*].id
    assign_public_ip = true
    security_groups  = [aws_security_group.kafka.id]
  }

  # Esto es lo que hace que la tarea se registre sola en el DNS privado al
  # arrancar, y se de de baja al pararse.
  service_registries {
    registry_arn = aws_service_discovery_service.kafka.arn
  }

  # Sin balanceador: Kafka no se expone, se descubre.

  lifecycle {
    ignore_changes = [desired_count]
  }

  tags = { Name = "${var.project}-kafka" }
}
