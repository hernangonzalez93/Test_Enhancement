# ---------------------------------------------------------------------------
# Red. Dos zonas de disponibilidad porque tanto el balanceador como el grupo de
# subredes de RDS lo exigen, aunque de momento no haya ni uno ni otro.
#
# NO hay NAT Gateway, y es una decision consciente: son unos 32 $/mes, el
# sobrecoste clasico de un entorno de aprendizaje. Las tareas viven en subredes
# publicas con IP publica y grupos de seguridad cerrados; la base de datos vive
# en subredes privadas sin salida a internet, que es donde debe estar.
# ---------------------------------------------------------------------------

data "aws_availability_zones" "disponibles" {
  state = "available"
}

resource "aws_vpc" "principal" {
  cidr_block           = var.vpc_cidr
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = { Name = "${var.project}-vpc" }
}

resource "aws_internet_gateway" "principal" {
  vpc_id = aws_vpc.principal.id

  tags = { Name = "${var.project}-igw" }
}

resource "aws_subnet" "publica" {
  count = 2

  vpc_id                  = aws_vpc.principal.id
  cidr_block              = cidrsubnet(var.vpc_cidr, 8, count.index)
  availability_zone       = data.aws_availability_zones.disponibles.names[count.index]
  map_public_ip_on_launch = true

  tags = { Name = "${var.project}-publica-${count.index + 1}" }
}

# Sin ruta a internet. RDS no necesita salir, y no poder salir es una propiedad
# de seguridad, no una carencia.
resource "aws_subnet" "privada" {
  count = 2

  vpc_id            = aws_vpc.principal.id
  cidr_block        = cidrsubnet(var.vpc_cidr, 8, count.index + 10)
  availability_zone = data.aws_availability_zones.disponibles.names[count.index]

  tags = { Name = "${var.project}-privada-${count.index + 1}" }
}

resource "aws_route_table" "publica" {
  vpc_id = aws_vpc.principal.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.principal.id
  }

  tags = { Name = "${var.project}-rt-publica" }
}

resource "aws_route_table_association" "publica" {
  count = length(aws_subnet.publica)

  subnet_id      = aws_subnet.publica[count.index].id
  route_table_id = aws_route_table.publica.id
}
