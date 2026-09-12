# ---------------------------------------------------------------------------
# Presupuesto y alarmas de coste
# ---------------------------------------------------------------------------
# Vive en el BOOTSTRAP y no en infra/ a proposito, y la razon se aprendio
# fallando: estaba en infra/, se lanzo el destroy, y la alarma de coste
# desaparecio junto con lo que vigilaba.
#
# Una red de seguridad no debe destruirse con aquello de lo que protege. Aqui
# sobrevive a cualquier ciclo de destruir y recrear, y cubre tambien lo que se
# cree a mano en la consola mientras la pila esta desmontada.
#
# Esta cuenta esta dedicada a este proyecto, asi que vigila el gasto total sin
# filtrar por etiqueta. Filtrar exigiria activarla antes como "cost allocation
# tag", con hasta 24 horas de espera.
#
# Los dos primeros presupuestos de una cuenta son gratis.
# ---------------------------------------------------------------------------

variable "budget_amount" {
  description = <<-EOT
    Limite mensual en dolares. La cifra debe reflejar lo que ESPERAS gastar, no
    lo que podrias permitirte: el presupuesto es un detector de sorpresas, y si
    se pone tan alto que nunca salta, no detecta nada.

    Hoy la infraestructura desplegada cuesta 0, asi que cualquier gasto real es
    una sorpresa que merece un aviso. Cuando lleguen la base de datos y el
    balanceador habra que subirlo deliberadamente.
  EOT
  type        = number
  default     = 10
}

variable "budget_email" {
  description = <<-EOT
    Correo que recibe los avisos. Nunca va en el codigo: el repositorio es
    publico y es un dato personal.

    Como el bootstrap se aplica a mano, se pone en terraform.tfvars —que esta
    en .gitignore— en lugar de llegar por un secreto de GitHub.
  EOT
  type        = string
  sensitive   = true
}

resource "aws_budgets_budget" "mensual" {
  name         = "${var.project}-mensual"
  budget_type  = "COST"
  limit_amount = var.budget_amount
  limit_unit   = "USD"
  time_unit    = "MONTHLY"

  cost_types {
    # LA LINEA IMPORTANTE. Por defecto los creditos se restan del coste, asi
    # que mientras queden creditos el presupuesto veria 0 y no avisaria nunca.
    # Con esto se vigila el gasto BRUTO: te enteras de lo que consumes mientras
    # los creditos todavia lo cubren, en vez de cuando se agoten.
    include_credit = false

    include_refund       = false
    include_subscription = true
    include_tax          = true
    use_blended          = false
  }

  # Mitad del limite: pronto, para enterarse de una tendencia.
  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 50
    threshold_type             = "PERCENTAGE"
    notification_type          = "ACTUAL"
    subscriber_email_addresses = [var.budget_email]
  }

  # Cuatro quintos: ya es un aviso serio.
  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 80
    threshold_type             = "PERCENTAGE"
    notification_type          = "ACTUAL"
    subscriber_email_addresses = [var.budget_email]
  }

  # El unico que avisa ANTES de que ocurra: AWS proyecta el gasto del mes segun
  # el ritmo actual. Si dejas algo encendido un viernes, este salta el sabado y
  # no el dia 28.
  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 100
    threshold_type             = "PERCENTAGE"
    notification_type          = "FORECASTED"
    subscriber_email_addresses = [var.budget_email]
  }
}

output "presupuesto" {
  description = "Nombre y limite del presupuesto configurado."
  value       = "${aws_budgets_budget.mensual.name}: ${var.budget_amount} USD/mes"
}
