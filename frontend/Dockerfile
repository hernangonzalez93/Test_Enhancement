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
# ESTA IMAGEN SOLO SE USA EN LOCAL. En AWS el frontal se sirve desde S3 con
# CloudFront delante: para tres ficheros estaticos, mantener un contenedor vivo
# es pagar ~9 $/mes y tener algo mas que acordarse de apagar.
FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY proxy_headers.inc /etc/nginx/conf.d/proxy_headers.inc
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
