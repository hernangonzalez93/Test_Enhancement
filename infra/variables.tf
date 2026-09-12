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
    Servicios que se despliegan en la nube, con el puerto por el que los expone
    el balanceador. Los puertos replican los del compose local, para que las
    pruebas de humo valgan cambiando solo el host.

    Es un MAPA y no una lista a proposito: con una lista el puerto salia del
    orden de los elementos, asi que reordenarla habria barajado los puertos sin
    que nadie lo notase hasta llamar al servicio equivocado.

    Deliberadamente no estan los seis: con creditos limitados se despliega un
    subconjunto que demuestra el pipeline entero, y la pila completa sigue
    viviendo en docker compose.
  EOT
  type        = map(number)
  default = {
    "rentals-api" = 5101
    "pricing-api" = 5102
    "fleet-api"   = 5103
  }
}

variable "log_retention_days" {
  description = "Retencion de los grupos de logs. Sin esto crecen para siempre."
  type        = number
  default     = 7
}
