# Bootstrap

Lo que tiene que existir **antes** de que GitHub Actions pueda aplicar nada:
el bucket del estado y el rol que Actions asume por OIDC.

Es el problema del huevo y la gallina de toda infraestructura como codigo: el
sitio donde se guarda el estado no puede estar en ese mismo estado.

Se aplica **una sola vez, desde tu equipo**, con estado local. Despues casi
nunca se toca.

```bash
export AWS_PROFILE=testenforce-b
cd infra/bootstrap
terraform init
terraform plan -out plan.tfplan
terraform apply plan.tfplan
```
