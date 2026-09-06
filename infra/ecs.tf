# ---------------------------------------------------------------------------
# Donde corren los contenedores
# ---------------------------------------------------------------------------
# El cluster y los roles no cuestan nada: lo que se paga son las tareas en
# marcha. Por eso el servicio nace con cero tareas y solo sube cuando alguien
# despliega o lo enciende a mano.
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
# descarga la imagen de ECR y crea el flujo de logs. El de TAREA lo usa tu
# proceso ya en marcha, para hablar con otros servicios de AWS.
#
# Confundirlos es el error mas comun al configurar ECS. Si un dia anades
# secretos de Parameter Store, el permiso para leerlos va en el de EJECUCION,
# porque quien los resuelve es el agente y no tu aplicacion.
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

# Sin politicas adjuntas a proposito: Pricing no llama a ningun servicio de
# AWS. Un rol vacio es la respuesta correcta cuando no se necesita nada.

# ---------------------------------------------------------------------------
# El servicio de Pricing
# ---------------------------------------------------------------------------

resource "aws_ecs_task_definition" "pricing" {
  family                   = "${var.project}-pricing-api"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.ejecucion.arn
  task_role_arn            = aws_iam_role.tarea.arn

  container_definitions = jsonencode([
    {
      name  = "pricing-api"
      image = "${aws_ecr_repository.servicio["pricing-api"].repository_url}:bootstrap"

      portMappings = [{ containerPort = 8080, protocol = "tcp" }]

      environment = [
        { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
        { name = "ASPNETCORE_URLS", value = "http://+:8080" }
      ]

      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.servicio["pricing-api"].name
          "awslogs-region"        = var.region
          "awslogs-stream-prefix" = "ecs"
        }
      }

      # La misma sonda que usa docker compose. ECS reinicia la tarea si falla.
      healthCheck = {
        command     = ["CMD-SHELL", "curl -fsS http://localhost:8080/health || exit 1"]
        interval    = 30
        timeout     = 5
        retries     = 3
        startPeriod = 30
      }
    }
  ])

  # La version que corre la decide el pipeline de despliegue, no este fichero.
  # Terraform describe la FORMA del servicio; que imagen concreta esta viva es
  # una decision de despliegue, y mezclarlas obligaria a un cambio de
  # infraestructura por cada version publicada.
  lifecycle {
    ignore_changes = [container_definitions]
  }

  tags = { Name = "${var.project}-pricing-api" }
}

resource "aws_ecs_service" "pricing" {
  name            = "pricing-api"
  cluster         = aws_ecs_cluster.principal.id
  task_definition = aws_ecs_task_definition.pricing.arn
  launch_type     = "FARGATE"

  # Nace apagado. Nada corre —y nada se paga— hasta que alguien despliegue o
  # lo encienda. Es tambien lo que hace que el apagado nocturno no se pelee
  # con Terraform: el estado deseado por defecto es cero.
  desired_count = 0

  network_configuration {
    subnets = aws_subnet.publica[*].id
    # Sin NAT, la tarea necesita IP publica para descargar la imagen de ECR.
    # No queda expuesta: el grupo de seguridad no admite ninguna entrada.
    assign_public_ip = true
    security_groups  = [aws_security_group.servicios.id]
  }

  lifecycle {
    ignore_changes = [
      # Lo cambian el pipeline de despliegue y el apagado nocturno.
      desired_count,
      task_definition,
    ]
  }

  tags = { Name = "${var.project}-pricing-api" }
}

output "cluster" {
  description = "Nombre del cluster de ECS."
  value       = aws_ecs_cluster.principal.name
}
