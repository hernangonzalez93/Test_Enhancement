# ---------------------------------------------------------------------------
# Registro de imagenes. Un repositorio por servicio.
#
# El almacenamiento se paga (0,10 $ por GB y mes) y las imagenes de .NET pesan
# unos 350 MB cada una. Sin politica de ciclo de vida, cada despliegue deja una
# imagen mas para siempre: en unos meses son decenas de GB de basura pagada.
# ---------------------------------------------------------------------------

resource "aws_ecr_repository" "servicio" {
  for_each = var.services

  name                 = "${var.project}/${each.key}"
  image_tag_mutability = "IMMUTABLE"

  # Sin esto, `terraform destroy` falla con RepositoryNotEmptyException en
  # cuanto el repositorio tenga una sola imagen, y deja la destruccion a
  # medias: el balanceador borrado y el registro no.
  #
  # Es seguro aqui porque las imagenes se reconstruyen desde el codigo en
  # cualquier momento: no son un dato, son un artefacto derivado. En un
  # registro del que dependieran otros, esto NO deberia estar.
  force_delete = true

  # Analisis de vulnerabilidades al subir. Es gratis en el nivel basico y no
  # hay razon para no tenerlo.
  image_scanning_configuration {
    scan_on_push = true
  }

  tags = { Name = "${var.project}-${each.key}" }
}

resource "aws_ecr_lifecycle_policy" "limpieza" {
  for_each = aws_ecr_repository.servicio

  repository = each.value.name

  policy = jsonencode({
    rules = [
      {
        rulePriority = 1
        description  = "Descartar imagenes sin etiqueta al dia siguiente"
        selection = {
          tagStatus   = "untagged"
          countType   = "sinceImagePushed"
          countUnit   = "days"
          countNumber = 1
        }
        action = { type = "expire" }
      },
      {
        rulePriority = 2
        description  = "Conservar solo las 10 ultimas versiones"
        selection = {
          tagStatus   = "any"
          countType   = "imageCountMoreThan"
          countNumber = 10
        }
        action = { type = "expire" }
      }
    ]
  })
}
