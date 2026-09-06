provider "aws" {
  region = var.region

  # A proposito NO se fija aqui el perfil: se toma de la variable de entorno
  # AWS_PROFILE. Asi el mismo codigo sirve desde tu equipo y desde GitHub
  # Actions, donde las credenciales llegaran por OIDC y no habra ningun perfil.

  # Estas etiquetas se aplican solas a todo recurso que las admita. Son lo que
  # permite filtrar el gasto por proyecto en los presupuestos y en Cost
  # Explorer: sin ellas, todo se mezcla en una sola factura.
  default_tags {
    tags = {
      Project     = "TestEnforce"
      Environment = var.environment
      ManagedBy   = "Terraform"
    }
  }
}
