output "vpc_id" {
  description = "Identificador de la VPC."
  value       = aws_vpc.principal.id
}

output "subredes_publicas" {
  description = "Subredes donde viviran las tareas."
  value       = aws_subnet.publica[*].id
}

output "subredes_privadas" {
  description = "Subredes para la base de datos."
  value       = aws_subnet.privada[*].id
}

output "repositorios_ecr" {
  description = "URLs de los repositorios, para etiquetar y subir imagenes."
  value       = { for k, v in aws_ecr_repository.servicio : k => v.repository_url }
}

output "grupos_de_seguridad" {
  description = "Identificadores de los grupos de seguridad."
  value = {
    balanceador   = aws_security_group.balanceador.id
    servicios     = aws_security_group.servicios.id
    base_de_datos = aws_security_group.base_de_datos.id
  }
}
