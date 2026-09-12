FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm install
COPY . .
RUN npm run build

# La variante "unprivileged" de nginx: corre como usuario normal y escucha en
# 8080 por defecto. La imagen oficial necesita root para enlazar el puerto 80,
# asi que este era el unico contenedor del proyecto que seguia siendo root
# despues de la fase 2.
#
# Esta imagen se usa en compose Y en AWS cuando `frontal = "nginx"`. Lo que
# cambia entre los dos sitios no es la imagen, son las variables de entorno:
# la configuracion se genera al arrancar a partir de la plantilla.
FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime

# En /etc/nginx/templates, no en conf.d: el arranque de la imagen procesa lo
# que hay aqui, sustituye las ${VARIABLES} y deja el resultado en conf.d.
COPY templates/default.conf.template /etc/nginx/templates/default.conf.template
COPY proxy_headers.inc /etc/nginx/conf.d/proxy_headers.inc
COPY --from=build /app/dist /usr/share/nginx/html

# Hace que la imagen lea su propio /etc/resolv.conf y exponga los servidores
# DNS del sistema como NGINX_LOCAL_RESOLVERS. Es la diferencia entre "funciona
# en compose" y "funciona donde lo pongas".
ENV NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1

EXPOSE 8080
