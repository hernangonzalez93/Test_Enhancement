# ---------------------------------------------------------------------------
# Grupos de seguridad. Crearlos no cuesta nada, y definirlos ahora deja el
# modelo de acceso escrito antes de que exista nada que proteger.
#
# La regla que los ordena: cada grupo permite entrada UNICAMENTE desde el grupo
# anterior, nunca desde un rango de direcciones. Referirse a un grupo de
# seguridad en vez de a una IP hace que la regla siga siendo cierta cuando las
# direcciones cambian, que en contenedores es constantemente.
# ---------------------------------------------------------------------------

resource "aws_security_group" "balanceador" {
  name        = "${var.project}-alb"
  description = "Entrada publica al balanceador"
  vpc_id      = aws_vpc.principal.id

  tags = { Name = "${var.project}-alb" }
}

# Un puerto por servicio, replicando la forma del compose local. Sin dominio
# propio no hay enrutado por host, y el enrutado por ruta solaparia los /health
# de los servicios entre si, rompiendo las pruebas de humo.
resource "aws_vpc_security_group_ingress_rule" "balanceador_entrada" {
  for_each = { for i, s in var.services : s => 5101 + i }

  security_group_id = aws_security_group.balanceador.id
  description       = "HTTP para ${each.key}"
  cidr_ipv4         = "0.0.0.0/0"
  from_port         = each.value
  to_port           = each.value
  ip_protocol       = "tcp"
}

resource "aws_vpc_security_group_egress_rule" "balanceador_salida" {
  security_group_id = aws_security_group.balanceador.id
  description       = "Hacia las tareas"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "-1"
}

resource "aws_security_group" "servicios" {
  name        = "${var.project}-servicios"
  description = "Tareas de ECS"
  vpc_id      = aws_vpc.principal.id

  tags = { Name = "${var.project}-servicios" }
}

# Solo el balanceador puede hablar con las tareas. Estan en subredes publicas
# por no pagar NAT, pero nadie de internet las alcanza directamente.
resource "aws_vpc_security_group_ingress_rule" "servicios_desde_balanceador" {
  security_group_id            = aws_security_group.servicios.id
  description                  = "Solo desde el balanceador"
  referenced_security_group_id = aws_security_group.balanceador.id
  from_port                    = 8080
  to_port                      = 8080
  ip_protocol                  = "tcp"
}

# Salida abierta: hace falta para descargar imagenes de ECR, resolver DNS y
# escribir en CloudWatch.
resource "aws_vpc_security_group_egress_rule" "servicios_salida" {
  security_group_id = aws_security_group.servicios.id
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "-1"
}

resource "aws_security_group" "base_de_datos" {
  name        = "${var.project}-bd"
  description = "PostgreSQL, alcanzable solo desde las tareas"
  vpc_id      = aws_vpc.principal.id

  tags = { Name = "${var.project}-bd" }
}

resource "aws_vpc_security_group_ingress_rule" "bd_desde_servicios" {
  security_group_id            = aws_security_group.base_de_datos.id
  description                  = "PostgreSQL desde las tareas"
  referenced_security_group_id = aws_security_group.servicios.id
  from_port                    = 5432
  to_port                      = 5432
  ip_protocol                  = "tcp"
}
