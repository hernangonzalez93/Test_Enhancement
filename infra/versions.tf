terraform {
  required_version = ">= 1.9"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # El estado es local por ahora. Es lo mas simple para empezar y no cuesta
  # nada, pero tiene un riesgo real: si pierdes el fichero, Terraform deja de
  # saber que recursos existen y te quedan huerfanos en AWS, cobrando.
  #
  # Se migra a S3 cuando haga falta, sin rehacer nada:
  #   terraform init -migrate-state
}
