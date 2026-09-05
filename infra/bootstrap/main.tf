terraform {
  required_version = ">= 1.9"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # Estado local a proposito. Esto es lo que CREA el bucket del estado, asi que
  # no puede guardarse en el. Es el unico sitio del proyecto donde el estado
  # local es la respuesta correcta y no un atajo.
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      Project   = "TestEnforce"
      ManagedBy = "Terraform"
      Scope     = "bootstrap"
    }
  }
}

variable "region" {
  type    = string
  default = "eu-west-1"
}

variable "project" {
  type    = string
  default = "testenforce"
}

variable "bucket_suffix" {
  description = <<-EOT
    Sufijo del nombre del bucket de estado. Solo hace falta cambiarlo si un
    nombre anterior quedo retenido: al borrar un bucket, S3 no libera su nombre
    al instante y crear otro igual falla con OperationAborted durante un rato.
    Cambiar el sufijo evita esperar, porque el nombre solo tiene que ser unico.
  EOT
  type        = string
  default     = "v2"
}

variable "expected_account_id" {
  description = <<-EOT
    Cuenta donde debe crearse todo. Si las credenciales apuntan a otra,
    Terraform se niega a continuar en el plan, antes de crear nada.
  EOT
  type        = string
  default     = "037169690600"
}

variable "github_repo" {
  description = "Repositorio autorizado a asumir los roles, en formato duenyo/repositorio."
  type        = string
  default     = "hernangonzalez93/Test_Enhancement"
}

data "aws_caller_identity" "actual" {}

# ---------------------------------------------------------------------------
# 0. Guardia: que las credenciales apunten a la cuenta que se espera
# ---------------------------------------------------------------------------
# Se evalua durante el PLAN, antes de crear nada. Existe porque el fallo
# contrario ya ocurrio: un plan guardado con un perfil se aplico con otro, y
# los recursos aparecieron en la cuenta equivocada. El nombre del bucket venia
# congelado en el fichero del plan, asi que ni siquiera resultaba evidente.
# ---------------------------------------------------------------------------

resource "terraform_data" "guardia_de_cuenta" {
  input = data.aws_caller_identity.actual.account_id

  lifecycle {
    precondition {
      condition     = data.aws_caller_identity.actual.account_id == var.expected_account_id
      error_message = <<-EOT
        Cuenta equivocada.

        Esperada : ${var.expected_account_id}
        Actual   : ${data.aws_caller_identity.actual.account_id}

        Revisa AWS_PROFILE. Si has abierto una terminal nueva, la variable se
        perdio y estas usando el perfil por defecto.
      EOT
    }
  }
}

# ---------------------------------------------------------------------------
# 1. El bucket del estado
# ---------------------------------------------------------------------------

resource "aws_s3_bucket" "estado" {
  bucket = "${var.project}-tfstate-${data.aws_caller_identity.actual.account_id}-${var.bucket_suffix}"

  # Se protege de un destroy accidental: el estado es lo unico que no se puede
  # reconstruir. Perderlo deja todos los recursos huerfanos en AWS.
  lifecycle {
    prevent_destroy = true
  }
}

# Versionado: si un apply corrompe el estado, se puede volver a la version
# anterior. Es la red de seguridad que justifica por si sola usar S3.
resource "aws_s3_bucket_versioning" "estado" {
  bucket = aws_s3_bucket.estado.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "estado" {
  bucket = aws_s3_bucket.estado.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

# El estado contiene identificadores y, segun los recursos, valores sensibles.
# Nunca debe ser publico.
resource "aws_s3_bucket_public_access_block" "estado" {
  bucket = aws_s3_bucket.estado.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# Las versiones antiguas del estado se acumulan. A los 30 dias ya no sirven
# para recuperar nada y solo ocupan.
resource "aws_s3_bucket_lifecycle_configuration" "estado" {
  bucket = aws_s3_bucket.estado.id

  rule {
    id     = "caducar-versiones-antiguas"
    status = "Enabled"

    filter {}

    noncurrent_version_expiration {
      noncurrent_days = 30
    }
  }
}

# ---------------------------------------------------------------------------
# 2. Confianza con GitHub
# ---------------------------------------------------------------------------

# Declara a AWS que confie en los tokens que emite GitHub Actions. Es lo que
# permite prescindir de claves de larga duracion: Actions presenta un token
# firmado por GitHub y AWS le entrega credenciales temporales.
resource "aws_iam_openid_connect_provider" "github" {
  url             = "https://token.actions.githubusercontent.com"
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = ["6938fd4d98bab03faadb97b34396831e3780aea1"]
}

# ---------------------------------------------------------------------------
# 3. Dos roles, no uno
# ---------------------------------------------------------------------------
# El rol que PLANIFICA solo lee, y lo puede asumir cualquier rama del
# repositorio. El que APLICA puede escribir, y solo lo asume el entorno `dev`,
# lo que en la practica significa el trabajo de despliegue tras fusionar.
#
# Separarlos importa: un plan corre sobre codigo que todavia no se ha revisado.
# Con un solo rol de escritura, cualquier cambio en el workflow dentro de un
# pull request podria modificar la infraestructura antes de que nadie lo mire.
# ---------------------------------------------------------------------------

data "aws_iam_policy_document" "confianza_plan" {
  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github.arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    # Cualquier referencia de ESTE repositorio, y de ningun otro.
    condition {
      test     = "StringLike"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_repo}:*"]
    }
  }
}

resource "aws_iam_role" "plan" {
  name               = "${var.project}-github-plan"
  description        = "Solo lectura, para terraform plan desde pull requests"
  assume_role_policy = data.aws_iam_policy_document.confianza_plan.json
}

resource "aws_iam_role_policy_attachment" "plan_lectura" {
  role       = aws_iam_role.plan.name
  policy_arn = "arn:aws:iam::aws:policy/ReadOnlyAccess"
}

data "aws_iam_policy_document" "confianza_apply" {
  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github.arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    # Exactamente el entorno `dev` de este repositorio. Un trabajo que no
    # declare `environment: dev` no puede asumir este rol, por mucho que se
    # ejecute en el mismo repositorio.
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_repo}:environment:dev"]
    }
  }
}

resource "aws_iam_role" "apply" {
  name               = "${var.project}-github-apply"
  description        = "Escritura, solo desde el entorno dev tras fusionar"
  assume_role_policy = data.aws_iam_policy_document.confianza_apply.json
}

# AdministratorAccess es amplio, y conviene decirlo en voz alta. Terraform crea
# roles y politicas de IAM, asi que necesita permisos sobre IAM; acotarlo de
# verdad es un trabajo considerable y facil de dejar a medias. Para un entorno
# de aprendizaje, la barrera real es la condicion de confianza de arriba: solo
# este repositorio, solo el entorno dev.
resource "aws_iam_role_policy_attachment" "apply_admin" {
  role       = aws_iam_role.apply.name
  policy_arn = "arn:aws:iam::aws:policy/AdministratorAccess"
}

# ---------------------------------------------------------------------------

output "bucket_estado" {
  description = "Nombre del bucket. Va en el bloque backend de infra/versions.tf."
  value       = aws_s3_bucket.estado.id
}

output "rol_plan" {
  description = "ARN del rol de solo lectura. Va como variable AWS_ROLE_PLAN."
  value       = aws_iam_role.plan.arn
}

output "rol_apply" {
  description = "ARN del rol de escritura. Va como variable AWS_ROLE_APPLY."
  value       = aws_iam_role.apply.arn
}
