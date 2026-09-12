# ---------------------------------------------------------------------------
# Donde corren los contenedores
# ---------------------------------------------------------------------------
# El cluster y los roles no cuestan nada: lo que se paga son las tareas en
# marcha. Por eso los servicios nacen con cero tareas y solo suben cuando
# alguien despliega o los enciende.
# ---------------------------------------------------------------------------

resource "aws_ecs_cluster" "principal" {
  name = var.project

  # Metricas de CPU, memoria y numero de tareas sin instrumentar nada.
  setting {
    name  = "containerInsights"
    value = "enhanced"
  }

  tags = { Name = var.project }
}

# ---------------------------------------------------------------------------
# Dos roles, y la diferencia importa
# ---------------------------------------------------------------------------
# El de EJECUCION lo usa el agente de ECS ANTES de que tu proceso exista:
# descarga la imagen de ECR, crea el flujo de logs y RESUELVE LOS SECRETOS. El
# de TAREA lo usa tu proceso ya en marcha, para hablar con otros servicios.
#
# Confundirlos es el error mas comun al configurar ECS: el permiso para leer
# una cadena de conexion de Parameter Store va en el de ejecucion, no en el de
# tarea, porque quien la lee es el agente y no la aplicacion.
# ---------------------------------------------------------------------------

data "aws_iam_policy_document" "confianza_ecs" {
  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "ejecucion" {
  name               = "${var.project}-ecs-ejecucion"
  description        = "Lo usa el agente de ECS para arrancar la tarea"
  assume_role_policy = data.aws_iam_policy_document.confianza_ecs.json
}

resource "aws_iam_role_policy_attachment" "ejecucion" {
  role       = aws_iam_role.ejecucion.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

resource "aws_iam_role" "tarea" {
  name               = "${var.project}-ecs-tarea"
  description        = "Lo usa el proceso ya en marcha"
  assume_role_policy = data.aws_iam_policy_document.confianza_ecs.json
}

# Sin politicas adjuntas: ninguno de estos servicios llama a la API de AWS. Un
# rol vacio es la respuesta correcta cuando no se necesita nada.

# ---------------------------------------------------------------------------
# Que necesita cada servicio
# ---------------------------------------------------------------------------
# Un unico sitio donde mirar para saber con que arranca cada uno. Anadir un
# servicio nuevo es anadir una entrada aqui y otra en var.services.
#
# Las direcciones internas usan el DNS privado y no el balanceador: una llamada
# de Rentals a Fleet no tiene por que salir a internet y volver a entrar.
# ---------------------------------------------------------------------------

locals {
  interno = aws_service_discovery_private_dns_namespace.interno.name

  # Lo que el balanceador publica. El frontal se suma solo cuando se sirve con
  # nginx; con CloudFront no hay tarea que exponer.
  expuestos = merge(
    var.services,
    var.frontal == "nginx" ? { frontend = 5173 } : {}
  )

  # Lo que comparten los que hablan con Kafka.
  kafka_comun = {
    "Kafka__BootstrapServers" = "kafka.${local.interno}:9092"
    "Kafka__Enabled"          = "true"
  }

  # Cada imagen se sonda con lo que garantiza tener. La de .NET lleva curl
  # instalado a proposito en su Dockerfile; la de nginx se apoya en el wget de
  # busybox, presente en cualquier variante alpine.
  #
  # (Comprobado: esta version de nginx tambien trae curl. Pero depender de algo
  # que la imagen no promete es como se acaba con una sonda que falla siempre
  # tras una actualizacion, y una sonda que falla siempre reinicia la tarea en
  # bucle sin que el problema este en la aplicacion.)
  sonda_dotnet = "curl -fsS http://localhost:8080/health || exit 1"
  sonda_nginx  = "wget -q --spider http://127.0.0.1:8080/health || exit 1"

  frontal_nginx = var.frontal != "nginx" ? {} : {
    "frontend" = {
      # A donde manda nginx cada ruta /api. En compose estos mismos nombres son
      # `rentals-api`, el DNS que Docker regala; aqui son los del DNS privado de
      # la VPC. La imagen es la misma: lo que cambia son estas variables.
      variables = {
        "API_RENTALS"       = "rentals.${local.interno}:8080"
        "API_FLEET"         = "fleet.${local.interno}:8080"
        "API_NOTIFICATIONS" = "notifications.${local.interno}:8080"
        "API_PRICING"       = "pricing.${local.interno}:8080"

        # Insurances y Billing no estan desplegados en AWS. Se les da el nombre
        # que TENDRIAN: el DNS no lo resuelve y la peticion muere en un 502, que
        # dice la verdad ("ese servicio no esta"). Dejar la variable sin valor
        # no seria mas honesto, seria peor: nginx no arrancaria.
        "API_INSURANCES" = "insurances.${local.interno}:8080"
        "API_BILLING"    = "billing.${local.interno}:8080"
      }
      secretos = {}
      migra    = false
      sonda    = local.sonda_nginx
    }
  }

  servicios = merge(local.frontal_nginx, {
    "pricing-api" = {
      # Sin estado, sin base de datos y sin mensajeria: el mas simple de todos,
      # y por eso fue el primero en desplegarse.
      variables = {}
      secretos  = {}
      migra     = false
      sonda     = local.sonda_dotnet
    }

    "fleet-api" = {
      variables = merge(local.kafka_comun, {
        "Kafka__Topic"   = "rental-events"
        "Kafka__GroupId" = "fleet-service"
      })
      secretos = {
        "ConnectionStrings__FleetDatabase" = aws_ssm_parameter.cadena_conexion["Fleet"].arn
      }
      migra = true
      sonda = local.sonda_dotnet
    }

    "notifications-api" = {
      # Sin base de datos: guarda los avisos en memoria. Es el consumidor mas
      # barato de desplegar, y el que deja ver el fan-out de Kafka: lee el
      # MISMO topic que Fleet, con su propio grupo y su propio marcador de
      # posicion, asi que los dos reciben cada evento.
      variables = merge(local.kafka_comun, {
        "Kafka__Topic"   = "rental-events"
        "Kafka__GroupId" = "notifications-service"
      })
      secretos = {}
      migra    = false
      sonda    = local.sonda_dotnet
    }

    "rentals-api" = {
      variables = merge(local.kafka_comun, {
        # Publica, no consume: no necesita grupo de consumo.
        "Kafka__RentalEventsTopic" = "rental-events"

        "Services__PricingBaseUrl" = "http://pricing.${local.interno}:8080"
        "Services__FleetBaseUrl"   = "http://fleet.${local.interno}:8080"
      })
      secretos = {
        "ConnectionStrings__RentalsDatabase" = aws_ssm_parameter.cadena_conexion["Rentals"].arn
      }
      migra = true
      sonda = local.sonda_dotnet
    }
  })
}

# ---------------------------------------------------------------------------
# Una definicion de tarea por servicio
# ---------------------------------------------------------------------------

resource "aws_ecs_task_definition" "servicio" {
  for_each = local.servicios

  family                   = "${var.project}-${each.key}"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.ejecucion.arn
  task_role_arn            = aws_iam_role.tarea.arn

  container_definitions = jsonencode([
    {
      name  = each.key
      image = "${aws_ecr_repository.servicio[each.key].repository_url}:bootstrap"

      portMappings = [{ containerPort = 8080, protocol = "tcp" }]

      environment = concat(
        [
          { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
          { name = "ASPNETCORE_URLS", value = "http://+:8080" }
        ],
        [for k, v in each.value.variables : { name = k, value = v }]
      )

      # `secrets` y no `environment`: ECS resuelve el valor al arrancar la
      # tarea y lo inyecta como una variable mas. La aplicacion no distingue,
      # pero el valor no aparece en la definicion de tarea ni en la consola.
      secrets = [for k, v in each.value.secretos : { name = k, valueFrom = v }]

      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.servicio[each.key].name
          "awslogs-region"        = var.region
          "awslogs-stream-prefix" = "ecs"
        }
      }

      healthCheck = {
        command     = ["CMD-SHELL", each.value.sonda]
        interval    = 30
        timeout     = 5
        retries     = 3
        startPeriod = 30
      }
    }
  ])

  # La version que corre la decide el pipeline de despliegue, no este fichero.
  lifecycle {
    ignore_changes = [container_definitions]
  }

  tags = { Name = "${var.project}-${each.key}" }
}

# ---------------------------------------------------------------------------
# Y un servicio por cada una
# ---------------------------------------------------------------------------

resource "aws_ecs_service" "servicio" {
  for_each = local.servicios

  name            = each.key
  cluster         = aws_ecs_cluster.principal.id
  task_definition = aws_ecs_task_definition.servicio[each.key].arn
  launch_type     = "FARGATE"

  # Nacen apagados: nada corre y nada se paga hasta que alguien los encienda.
  desired_count = 0

  network_configuration {
    subnets = aws_subnet.publica[*].id
    # Sin NAT, la tarea necesita IP publica para descargar la imagen de ECR.
    # No queda expuesta: el grupo de seguridad solo admite al balanceador y a
    # las demas tareas.
    assign_public_ip = true
    security_groups  = [aws_security_group.servicios.id]
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.servicio[each.key].arn
    container_name   = each.key
    container_port   = 8080
  }

  # Ademas del balanceador, cada servicio se registra en el DNS privado. Es lo
  # que permite que Rentals llame a `fleet.testenforce.local` sin salir a
  # internet ni conocer ninguna direccion.
  service_registries {
    registry_arn = aws_service_discovery_service.servicio[each.key].arn
  }

  # Una aplicacion .NET tarda unos segundos en levantar. Sin esta gracia, el
  # balanceador la declararia enferma antes de arrancar y ECS la mataria en
  # bucle, sin que el problema fuese la aplicacion.
  health_check_grace_period_seconds = 60

  depends_on = [aws_lb_listener.servicio]

  lifecycle {
    ignore_changes = [
      # Lo cambian el pipeline de despliegue y el apagado nocturno.
      desired_count,
      task_definition,
    ]
  }

  tags = { Name = "${var.project}-${each.key}" }
}

# ---------------------------------------------------------------------------
# Pricing ya existia con otro nombre en el estado
# ---------------------------------------------------------------------------
# Estos bloques le dicen a Terraform "es el mismo recurso, lo he renombrado".
# Sin ellos destruiria el servicio de Pricing y crearia otro identico, con su
# corte de servicio y su registro DNS nuevo, para nada.
# ---------------------------------------------------------------------------

moved {
  from = aws_ecs_task_definition.pricing
  to   = aws_ecs_task_definition.servicio["pricing-api"]
}

moved {
  from = aws_ecs_service.pricing
  to   = aws_ecs_service.servicio["pricing-api"]
}

output "cluster" {
  description = "Nombre del cluster de ECS."
  value       = aws_ecs_cluster.principal.name
}
