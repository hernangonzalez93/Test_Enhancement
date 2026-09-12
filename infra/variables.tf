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
    "rentals-api"       = 5101
    "pricing-api"       = 5102
    "fleet-api"         = 5103
    "notifications-api" = 5104
  }
}

variable "log_retention_days" {
  description = "Retencion de los grupos de logs. Sin esto crecen para siempre."
  type        = number
  default     = 7
}

variable "frontal" {
  description = <<-EOT
    Como se sirve el frontal. Es un interruptor, no dos configuraciones:

      nginx       una tarea de ECS mas, detras del balanceador. Cuesta ~9 $/mes
                  y hay que acordarse de apagarla, pero funciona hoy.

      cloudfront  los ficheros en S3 con CloudFront delante. Practicamente
                  gratis y sin nada que apagar, pero AWS exige verificar la
                  cuenta antes de permitir crear distribuciones.

    Cambiar de uno a otro es cambiar este valor: el codigo de los dos caminos
    convive, y solo se crea el del modo activo.
  EOT
  type        = string
  default     = "nginx"

  validation {
    condition     = contains(["nginx", "cloudfront"], var.frontal)
    error_message = "Solo vale 'nginx' o 'cloudfront'."
  }
}
