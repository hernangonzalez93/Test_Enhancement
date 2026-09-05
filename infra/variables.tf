variable "region" {
  description = "Region de AWS donde vive todo."
  type        = string
  default     = "eu-west-1"
}

variable "environment" {
  description = "Nombre del entorno. Por ahora solo existe dev."
  type        = string
  default     = "dev"
}

variable "project" {
  description = "Prefijo de los nombres de recurso."
  type        = string
  default     = "testenforce"
}

variable "vpc_cidr" {
  description = "Rango de la VPC."
  type        = string
  default     = "10.20.0.0/16"
}

variable "services" {
  description = <<-EOT
    Servicios que se despliegan en la nube. Deliberadamente NO son los seis:
    con creditos limitados se despliega un subconjunto que demuestra el pipeline
    entero, y la pila completa sigue viviendo en docker compose. Anadir uno mas
    es anadirlo a esta lista.
  EOT
  type        = list(string)
  default     = ["rentals-api", "pricing-api"]
}

variable "log_retention_days" {
  description = "Retencion de los grupos de logs. Sin esto crecen para siempre."
  type        = number
  default     = 7
}
