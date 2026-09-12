"""Convierte un plan de Terraform en un resumen legible para un comentario de PR.

El plan crudo es exacto pero ilegible: cientos de lineas de atributos internos.
Quien revisa un pull request necesita responder tres preguntas —que se crea,
que se destruye, y si algo empieza a costar dinero— y ninguna se contesta
comodamente leyendo JSON.

Se usa asi:
    terraform show -json plan.tfplan | python resumen_plan.py > comentario.md
"""

import json
import sys
from collections import Counter

# Como se llaman las cosas en castellano, agrupadas por area. Lo que no este
# aqui se muestra con su tipo tecnico: es preferible a ocultarlo.
CATALOGO = {
    "aws_vpc": ("Red", "red virtual"),
    "aws_subnet": ("Red", "subred"),
    "aws_internet_gateway": ("Red", "pasarela de internet"),
    "aws_route_table": ("Red", "tabla de rutas"),
    "aws_route_table_association": ("Red", "asociacion de tabla de rutas"),
    "aws_nat_gateway": ("Red", "pasarela NAT"),
    "aws_security_group": ("Seguridad", "grupo de seguridad"),
    "aws_vpc_security_group_ingress_rule": ("Seguridad", "regla de entrada"),
    "aws_vpc_security_group_egress_rule": ("Seguridad", "regla de salida"),
    "aws_iam_role": ("Permisos", "rol"),
    "aws_iam_role_policy_attachment": ("Permisos", "politica asociada a un rol"),
    "aws_iam_openid_connect_provider": ("Permisos", "proveedor de identidad"),
    "aws_iam_role_policy": ("Permisos", "politica propia de un rol"),
    "aws_iam_policy": ("Permisos", "politica"),
    "aws_scheduler_schedule": ("Programacion", "cita programada"),
    "aws_service_discovery_private_dns_namespace": ("Descubrimiento", "espacio de nombres DNS"),
    "aws_service_discovery_service": ("Descubrimiento", "servicio descubrible"),
    "aws_cloudwatch_event_rule": ("Programacion", "regla programada"),
    "aws_appautoscaling_target": ("Computo", "objetivo de escalado"),
    "aws_appautoscaling_policy": ("Computo", "politica de escalado"),
    "aws_ecr_repository": ("Imagenes", "repositorio de imagenes"),
    "aws_ecr_lifecycle_policy": ("Imagenes", "politica de limpieza de imagenes"),
    "aws_cloudwatch_log_group": ("Observabilidad", "grupo de logs"),
    "aws_cloudwatch_metric_alarm": ("Observabilidad", "alarma"),
    "aws_db_instance": ("Datos", "base de datos"),
    "aws_db_subnet_group": ("Datos", "grupo de subredes de base de datos"),
    "aws_s3_bucket": ("Datos", "bucket de S3"),
    "aws_lb": ("Entrada", "balanceador de carga"),
    "aws_lb_target_group": ("Entrada", "grupo de destinos"),
    "aws_lb_listener": ("Entrada", "escuchador"),
    "aws_ecs_cluster": ("Computo", "cluster de ECS"),
    "aws_ecs_service": ("Computo", "servicio de ECS"),
    "aws_ecs_task_definition": ("Computo", "definicion de tarea"),
    "aws_budgets_budget": ("Costes", "presupuesto"),
    "terraform_data": ("Control", "comprobacion interna"),
}

# Recursos que empiezan a facturar en cuanto existen, aunque nadie los use.
# Es la unica pregunta economica que importa al revisar un plan.
FACTURABLES = {
    "aws_db_instance": "por hora encendida",
    "aws_lb": "por hora, unos 16 $/mes",
    "aws_nat_gateway": "por hora, unos 32 $/mes",
    "aws_instance": "por hora encendida",
    "aws_eip": "si no esta asociada a nada",
    "aws_ecs_service": "solo por las tareas que tenga en marcha; con cero, nada",
    "aws_elasticache_cluster": "por hora encendida",
}

PLURALES = {
    "red virtual": "redes virtuales",
    "subred": "subredes",
    "pasarela de internet": "pasarelas de internet",
    "tabla de rutas": "tablas de rutas",
    "asociacion de tabla de rutas": "asociaciones de tabla de rutas",
    "pasarela NAT": "pasarelas NAT",
    "grupo de seguridad": "grupos de seguridad",
    "regla de entrada": "reglas de entrada",
    "regla de salida": "reglas de salida",
    "rol": "roles",
    "politica asociada a un rol": "politicas asociadas a roles",
    "proveedor de identidad": "proveedores de identidad",
    "politica propia de un rol": "politicas propias de roles",
    "politica": "politicas",
    "cita programada": "citas programadas",
    "espacio de nombres DNS": "espacios de nombres DNS",
    "servicio descubrible": "servicios descubribles",
    "regla programada": "reglas programadas",
    "objetivo de escalado": "objetivos de escalado",
    "politica de escalado": "politicas de escalado",
    "repositorio de imagenes": "repositorios de imagenes",
    "politica de limpieza de imagenes": "politicas de limpieza de imagenes",
    "grupo de logs": "grupos de logs",
    "alarma": "alarmas",
    "base de datos": "bases de datos",
    "grupo de subredes de base de datos": "grupos de subredes de base de datos",
    "bucket de S3": "buckets de S3",
    "balanceador de carga": "balanceadores de carga",
    "grupo de destinos": "grupos de destinos",
    "escuchador": "escuchadores",
    "cluster de ECS": "clusteres de ECS",
    "servicio de ECS": "servicios de ECS",
    "definicion de tarea": "definiciones de tarea",
    "presupuesto": "presupuestos",
    "comprobacion interna": "comprobaciones internas",
}


def nombrar(tipo):
    return CATALOGO.get(tipo, ("Otros", tipo))


def contar(n, singular):
    return f"{n} {singular if n == 1 else PLURALES.get(singular, singular)}"


def agrupar(cambios, accion):
    """Agrupa por area los recursos que sufren la accion dada."""
    por_area = {}
    for c in cambios:
        if accion not in c["change"]["actions"]:
            continue
        area, nombre = nombrar(c["type"])
        por_area.setdefault(area, Counter())[nombre] += 1
    return por_area


def render(por_area):
    lineas = []
    for area in sorted(por_area):
        piezas = [contar(n, nombre) for nombre, n in sorted(por_area[area].items())]
        lineas.append(f"- **{area}**: {', '.join(piezas)}")
    return "\n".join(lineas)


def main():
    plan = json.load(sys.stdin)
    cambios = [
        c for c in plan.get("resource_changes", [])
        if c["change"]["actions"] != ["no-op"]
    ]

    crear = sum(1 for c in cambios if "create" in c["change"]["actions"])
    borrar = sum(1 for c in cambios if "delete" in c["change"]["actions"])
    tocar = sum(1 for c in cambios if c["change"]["actions"] == ["update"])

    salida = ["## Plan de infraestructura", ""]

    if not cambios:
        salida += [
            "**No hay nada que cambiar.** Lo que hay desplegado ya coincide con lo que "
            "describe el codigo.",
            "",
            "Fusionar este pull request no tocara la infraestructura.",
        ]
        print("\n".join(salida))
        return

    # Una frase que resuma el conjunto, antes de cualquier detalle.
    partes = []
    if crear:
        partes.append(f"**crear {crear}**")
    if tocar:
        partes.append(f"**modificar {tocar}**")
    if borrar:
        partes.append(f"**eliminar {borrar}**")
    total = crear + tocar + borrar
    verbo = "Se va a" if total == 1 else "Se van a"
    sustantivo = "recurso" if total == 1 else "recursos"
    salida += [f"{verbo} {', '.join(partes)} {sustantivo}.", ""]

    # Lo destructivo primero: es lo que hay que mirar con mas cuidado.
    if borrar:
        salida += ["### Se elimina", "", render(agrupar(cambios, "delete")), ""]
        salida += [
            "> Comprueba que la eliminacion es intencionada. Un recurso con datos "
            "dentro no se recupera despues.",
            "",
        ]

    if crear:
        salida += ["### Se crea", "", render(agrupar(cambios, "create")), ""]

    if tocar:
        detalle = []
        for c in cambios:
            if c["change"]["actions"] == ["update"]:
                _, nombre = nombrar(c["type"])
                detalle.append(f"- {nombre} `{c['name']}`")
        salida += ["### Se modifica", "", "\n".join(detalle), ""]

    # La pregunta economica, contestada explicitamente.
    nuevos = {c["type"] for c in cambios if "create" in c["change"]["actions"]}
    cuestan = sorted(nuevos & set(FACTURABLES))

    salida += ["### Coste", ""]
    if cuestan:
        salida.append("Estos recursos **empiezan a facturar en cuanto existan**:")
        salida.append("")
        for tipo in cuestan:
            _, nombre = nombrar(tipo)
            salida.append(f"- {nombre} — {FACTURABLES[tipo]}")
        salida += ["", "El resto no cuesta nada por existir."]
    else:
        salida.append(
            "**Ninguno de estos recursos se factura por existir.** Se paga por el uso "
            "que se les de, no por tenerlos creados."
        )
    salida.append("")

    salida += ["---", "", "Al fusionar este pull request, esto se aplica automaticamente."]
    print("\n".join(salida))


if __name__ == "__main__":
    main()
