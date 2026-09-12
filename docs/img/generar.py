# -*- coding: utf-8 -*-
"""Saca los diagramas de arquitectura.html a ficheros .svg que GitHub sabe pintar.

    python docs/img/generar.py

La pagina resuelve sus colores con variables CSS que viven en su hoja de
estilos. Un .svg suelto no tiene esa hoja, asi que aqui se hornean: un fichero
por tema, y ARQUITECTURA.md elige con <picture> segun el tema de quien lee.

Los .svg no se editan a mano: se edita arquitectura.html y se vuelve a correr
esto.
"""
import pathlib
import re
from html.entities import html5 as ENTIDADES

AQUI = pathlib.Path(__file__).resolve().parent
ORIGEN = AQUI.parent / "arquitectura.html"
DESTINO = AQUI

html = ORIGEN.read_text(encoding="utf-8")

SANS = "'IBM Plex Sans', system-ui, -apple-system, 'Segoe UI', sans-serif"
MONO = "'IBM Plex Mono', ui-monospace, Consolas, monospace"


def tokens(bloque):
    """Los --nombre: valor; de un bloque de CSS."""
    return dict(re.findall(r"--([a-z-]+):\s*([^;]+);", bloque))


# :root, hasta el primer cierre
i = html.index(":root {")
claro = tokens(html[i:html.index("\n  }", i)])

# el bloque de dentro de @media
i = html.index('@media (prefers-color-scheme: dark)')
oscuro = tokens(html[i:html.index("\n    }", i)])

CLASES = """
  <style>
    .d-titulo {{ font-family: {sans}; font-size: 12px; font-weight: 600; fill: {ink}; }}
    .d-zona   {{ font-family: {sans}; font-size: 10.5px; font-weight: 600; fill: {muted}; letter-spacing: 0.06em; }}
    .d-cidr   {{ font-family: {mono}; font-size: 10px; fill: {muted}; }}
    .d-caja   {{ font-family: {sans}; font-size: 11.5px; font-weight: 500; fill: {ink}; }}
    .d-sub    {{ font-family: {mono}; font-size: 9.5px; fill: {muted}; }}
    .d-flecha {{ font-family: {sans}; font-size: 10px; fill: {accent}; font-weight: 500; }}
    .d-nota   {{ font-family: {sans}; font-size: 10px; fill: {muted}; }}
  </style>
"""

NOMBRES = ["topologia", "puertos", "grupos-de-seguridad", "recorrido", "frontal"]

svgs = re.findall(r'(<svg viewBox="0 0 (\d+) (\d+)".*?</svg>)', html, re.S)
assert len(svgs) == len(NOMBRES), "esperaba %d diagramas, encontre %d" % (len(NOMBRES), len(svgs))

DESTINO.mkdir(parents=True, exist_ok=True)
escritos = []

for (cuerpo, ancho, alto), nombre in zip(svgs, NOMBRES):
    for sufijo, tema in (("", claro), ("-oscuro", oscuro)):
        s = cuerpo

        # Un fondo propio: al pintarse como imagen suelta, debajo no hay lienzo.
        s = s.replace(
            '>',
            ' xmlns="http://www.w3.org/2000/svg" width="%s" height="%s">%s'
            '  <rect width="%s" height="%s" fill="%s"/>' % (
                ancho, alto,
                CLASES.format(sans=SANS, mono=MONO, ink=tema["ink"],
                              muted=tema["muted"], accent=tema["accent"]),
                ancho, alto, tema["surface"]),
            1)

        # Y los colores, horneados
        s = re.sub(r"var\(--([a-z-]+)\)", lambda m: tema[m.group(1)], s)
        assert "var(--" not in s, "han quedado variables sin resolver en " + nombre

        # XML solo conoce cinco entidades con nombre; &middot; y companía son
        # de HTML y aqui romperian el fichero. Se pasan a su caracter literal,
        # que en UTF-8 no necesita entidad ninguna.
        s = re.sub(
            r"&([a-zA-Z][a-zA-Z0-9]*);",
            lambda m: m.group(0) if m.group(1) in ("amp", "lt", "gt", "quot", "apos")
            else ENTIDADES.get(m.group(1) + ";", m.group(0)),
            s)

        f = DESTINO / ("%s%s.svg" % (nombre, sufijo))
        f.write_text(s + "\n", encoding="utf-8", newline="\n")
        escritos.append(f.name)

print("escritos %d ficheros:" % len(escritos))
for n in escritos:
    print("  " + n)
