# ---------------------------------------------------------------------------
# El balanceador
# ---------------------------------------------------------------------------
# Resuelve dos problemas: las tareas cambian de IP en cada despliegue, y en
# cuanto haya mas de una hace falta alguien que reparta.
#
# ATENCION AL COSTE: un balanceador NO se puede apagar. O existe o no existe,
# y mientras exista se paga por hora, unos 16 $/mes. Es el unico recurso de
# este proyecto que no se puede poner a cero sin destruirlo, y por eso el
# interruptor del dia a dia tiene dos niveles.
# ---------------------------------------------------------------------------

resource "aws_lb" "principal" {
  name               = var.project
  load_balancer_type = "application"
  internal           = false
  subnets            = aws_subnet.publica[*].id
  security_groups    = [aws_security_group.balanceador.id]

  # Cortar una conexion a medias no ayuda a nadie: se espera a que terminen.
  enable_deletion_protection = false
  idle_timeout               = 60

  tags = { Name = var.project }
}

# ---------------------------------------------------------------------------
# Un grupo de destinos por servicio
# ---------------------------------------------------------------------------

resource "aws_lb_target_group" "servicio" {
  for_each = var.services

  name     = "${var.project}-${replace(each.key, "-api", "")}"
  port     = 8080
  protocol = "HTTP"
  vpc_id   = aws_vpc.principal.id

  # "ip" y no "instance" porque con Fargate cada tarea tiene su propia
  # interfaz de red: no hay maquinas que registrar, hay direcciones.
  target_type = "ip"

  # Esta sonda es distinta de la del contenedor. La del contenedor comprueba
  # que el proceso vive; esta comprueba que se puede LLEGAR hasta el, asi que
  # detecta ademas problemas de red o de grupos de seguridad.
  #
  # Se usa /health y no /health/ready a proposito: aqui interesa "responde",
  # no "puede trabajar". Si un servicio pierde la base de datos, sigue siendo
  # capaz de contestar que esta mal, y eso es mas util que sacarlo del
  # balanceador y quedarse sin nadie atendiendo.
  health_check {
    enabled             = true
    path                = "/health"
    protocol            = "HTTP"
    matcher             = "200"
    interval            = 30
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }

  # Cuando se retira una tarea, se le dan 30 segundos para terminar lo que
  # tenga entre manos antes de cerrarla. Por defecto son 300, una eternidad
  # en cada despliegue.
  deregistration_delay = 30

  tags = { Name = "${var.project}-${each.key}" }
}

# ---------------------------------------------------------------------------
# Un escuchador por servicio, en su puerto
# ---------------------------------------------------------------------------
# Sin dominio propio no hay enrutado por nombre, asi que se separa por puerto,
# replicando la forma del compose local: 5101 Rentals, 5102 Pricing. Asi las
# pruebas de humo siguen valiendo cambiando solo el host.
# ---------------------------------------------------------------------------

resource "aws_lb_listener" "servicio" {
  for_each = var.services

  load_balancer_arn = aws_lb.principal.arn
  port              = each.value
  protocol          = "HTTP"

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.servicio[each.key].arn
  }

  tags = { Name = "${var.project}-${each.key}" }
}

output "balanceador" {
  description = "Direccion estable del balanceador."
  value       = aws_lb.principal.dns_name
}

output "urls" {
  description = "Donde responde cada servicio."
  value = {
    for s, p in var.services : s => "http://${aws_lb.principal.dns_name}:${p}"
  }
}
