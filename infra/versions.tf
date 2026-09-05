terraform {
  required_version = ">= 1.9"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # El estado vive en S3 porque GitHub Actions tiene que poder leerlo y
  # escribirlo: con estado local, un workflow no ve nada de lo aplicado.
  #
  # El nombre del bucket NO esta aqui a proposito. Lleva dentro el numero de
  # cuenta, y este repositorio es publico. Se pasa al inicializar:
  #   terraform init -backend-config="bucket=<nombre>"
  #
  # use_lockfile evita que dos apply simultaneos se pisen, sin necesidad de
  # una tabla de DynamoDB aparte (Terraform 1.10 en adelante).
  backend "s3" {
    key          = "dev/terraform.tfstate"
    region       = "eu-west-1"
    encrypt      = true
    use_lockfile = true
  }
}
