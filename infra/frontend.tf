# ---------------------------------------------------------------------------
# El frontal: S3 con CloudFront delante
# ---------------------------------------------------------------------------
# Sin contenedor. Servir tres ficheros estaticos con un nginx vivo cuesta unos
# 9 $/mes y anade algo mas que acordarse de apagar; CloudFront los reparte
# desde el borde por practicamente nada, y su capa gratuita —1 TB de salida y
# 10 millones de peticiones al mes— es permanente, no de 12 meses.
#
# CloudFront reparte por RUTA entre dos origenes:
#
#   /api/rentals/*  ─►  balanceador, puerto 5101
#   /api/vehicles/* ─►  balanceador, puerto 5103
#   todo lo demas   ─►  S3
#
# Para el navegador es un unico origen, asi que no hay CORS que resolver. Es
# exactamente lo que hace nginx en local, pero sin proceso que mantener.
# ---------------------------------------------------------------------------

# El bucket se crea en los DOS modos. Vacio es gratis, y tenerlo ya creado hace
# que pasar a CloudFront sea solo anadir la distribucion.
resource "aws_s3_bucket" "frontend" {
  bucket = "${var.project}-frontend-${data.aws_caller_identity.actual.account_id}"

  # Los ficheros se reconstruyen desde el codigo en cada despliegue: no son un
  # dato, asi que destruirlo con contenido es seguro.
  force_destroy = true

  tags = { Name = "${var.project}-frontend" }
}

# El bucket NO es publico. CloudFront accede con una identidad propia, y nadie
# mas puede leerlo ni siquiera conociendo la URL. Es la diferencia entre
# "publicar un bucket" y "servir un sitio": lo primero deja la puerta abierta.
resource "aws_s3_bucket_public_access_block" "frontend" {
  bucket = aws_s3_bucket.frontend.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_cloudfront_origin_access_control" "frontend" {
  count = var.frontal == "cloudfront" ? 1 : 0

  name                              = "${var.project}-frontend"
  description                       = "Acceso de CloudFront al bucket del frontal"
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

data "aws_iam_policy_document" "frontend" {
  count = var.frontal == "cloudfront" ? 1 : 0

  statement {
    effect    = "Allow"
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.frontend.arn}/*"]

    principals {
      type        = "Service"
      identifiers = ["cloudfront.amazonaws.com"]
    }

    # Y solo ESTA distribucion, no cualquiera de la cuenta.
    condition {
      test     = "StringEquals"
      variable = "AWS:SourceArn"
      values   = [aws_cloudfront_distribution.frontend[0].arn]
    }
  }
}

resource "aws_s3_bucket_policy" "frontend" {
  count = var.frontal == "cloudfront" ? 1 : 0

  bucket = aws_s3_bucket.frontend.id
  policy = data.aws_iam_policy_document.frontend[0].json
}

# ---------------------------------------------------------------------------
# Que ruta va a que servicio
# ---------------------------------------------------------------------------
# Replica lo que hace nginx en local. Cada entrada se convierte en un origen
# apuntando al balanceador por el puerto del servicio, y en un comportamiento
# que enruta esa ruta hacia el.
# ---------------------------------------------------------------------------

locals {
  rutas_api = {
    "/api/rentals*"       = "rentals-api"
    "/api/vehicles*"      = "fleet-api"
    "/api/notifications*" = "notifications-api"
    "/api/quotes*"        = "pricing-api"
    "/api/pricing*"       = "pricing-api"
  }

  # Un origen por servicio, no por ruta: dos rutas de Pricing comparten origen.
  origenes_api = toset(values(local.rutas_api))
}

resource "aws_cloudfront_distribution" "frontend" {
  count = var.frontal == "cloudfront" ? 1 : 0

  enabled             = true
  default_root_object = "index.html"
  comment             = "${var.project} - frontal"

  # Solo Europa y Norteamerica: las regiones lejanas encarecen sin aportar
  # nada a un entorno de aprendizaje.
  price_class = "PriceClass_100"

  # ---- Origen 1: los ficheros estaticos ----
  origin {
    origin_id                = "s3"
    domain_name              = aws_s3_bucket.frontend.bucket_regional_domain_name
    origin_access_control_id = aws_cloudfront_origin_access_control.frontend[0].id
  }

  # ---- Origen 2 en adelante: el balanceador, un puerto por servicio ----
  dynamic "origin" {
    for_each = local.origenes_api

    content {
      origin_id   = origin.value
      domain_name = aws_lb.principal.dns_name

      custom_origin_config {
        http_port  = var.services[origin.value]
        https_port = 443
        # El balanceador no tiene certificado —no hay dominio propio—, asi que
        # CloudFront le habla por HTTP. El tramo navegador-CloudFront SI va
        # cifrado: la parte publica del camino esta protegida.
        origin_protocol_policy = "http-only"
        origin_ssl_protocols   = ["TLSv1.2"]
      }
    }
  }

  # ---- Lo que no case con nada: el SPA ----
  default_cache_behavior {
    target_origin_id       = "s3"
    allowed_methods        = ["GET", "HEAD", "OPTIONS"]
    cached_methods         = ["GET", "HEAD"]
    viewer_protocol_policy = "redirect-to-https"

    # Politicas gestionadas de AWS, en vez de escribirlas a mano:
    # CachingOptimized guarda agresivamente, que es lo correcto para ficheros
    # con hash en el nombre como los que genera Vite.
    cache_policy_id = "658327ea-f89d-4fab-a63d-7e88639e58f6"
  }

  # ---- Y las rutas de API, que NO se cachean ----
  dynamic "ordered_cache_behavior" {
    for_each = local.rutas_api

    content {
      path_pattern           = ordered_cache_behavior.key
      target_origin_id       = ordered_cache_behavior.value
      allowed_methods        = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
      cached_methods         = ["GET", "HEAD"]
      viewer_protocol_policy = "redirect-to-https"

      # CachingDisabled. Una respuesta de API cacheada es un error esperando:
      # verias la renta de otro, o la tuya de hace cinco minutos.
      cache_policy_id = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad"

      # AllViewerExceptHostHeader: reenvia cabeceras, cookies y parametros al
      # balanceador, pero reescribe Host. Sin eso, el balanceador recibiria el
      # Host de CloudFront y no sabria a quien va dirigido.
      origin_request_policy_id = "b689b0a8-53d0-40ab-baf2-68738e2966ac"
    }
  }

  # El enrutado del SPA ocurre en el navegador, asi que una ruta como
  # /rentals/123 no existe como fichero en S3. Sin esto, recargar esa pagina
  # daria un 403 en lugar de cargar la aplicacion.
  custom_error_response {
    error_code         = 403
    response_code      = 200
    response_page_path = "/index.html"
  }

  custom_error_response {
    error_code         = 404
    response_code      = 200
    response_page_path = "/index.html"
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  # Sin dominio propio se usa el certificado de CloudFront, que cubre
  # *.cloudfront.net.
  viewer_certificate {
    cloudfront_default_certificate = true
  }

  tags = { Name = "${var.project}-frontend" }
}

output "frontend" {
  description = "Donde se abre la aplicacion, sea cual sea el modo activo."
  value = var.frontal == "cloudfront" ? (
    "https://${aws_cloudfront_distribution.frontend[0].domain_name}"
    ) : (
    "http://${aws_lb.principal.dns_name}:5173"
  )
}

output "bucket_frontend" {
  description = "Bucket donde el pipeline sube los ficheros construidos."
  value       = aws_s3_bucket.frontend.id
}

output "distribucion_frontend" {
  description = "Identificador de la distribucion, para invalidar la cache al desplegar."
  value       = var.frontal == "cloudfront" ? aws_cloudfront_distribution.frontend[0].id : null
}

output "modo_frontal" {
  description = "Como se esta sirviendo el frontal ahora mismo."
  value       = var.frontal
}
