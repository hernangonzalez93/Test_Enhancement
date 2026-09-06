# ---------------------------------------------------------------------------
# Guardia de cuenta
# ---------------------------------------------------------------------------
# Se evalua durante el PLAN, antes de crear nada. Existe porque el fallo
# contrario ya ocurrio en el bootstrap: un plan guardado con un perfil se
# aplico con otro, y los recursos aparecieron en la cuenta equivocada.
#
# Aqui importa mas todavia, porque esta es la configuracion que crea recursos
# que cuestan dinero.
# ---------------------------------------------------------------------------

data "aws_caller_identity" "actual" {}

variable "expected_account_id" {
  description = "Cuenta donde debe desplegarse. Si no coincide, no se continua."
  type        = string
  default     = "037169690600"
}

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
