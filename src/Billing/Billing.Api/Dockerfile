# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Primero solo los ficheros que definen dependencias: si no cambian, Docker
# reutiliza la capa de restore y la compilacion es mucho mas rapida.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/

RUN dotnet restore "src/Billing/Billing.Api/Billing.Api.csproj"
RUN dotnet publish "src/Billing/Billing.Api/Billing.Api.csproj" -c Release -o /app/publish --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl se instala solo para que docker compose pueda sondear /health.
RUN apt-get update     && apt-get install -y --no-install-recommends curl     && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080     ASPNETCORE_ENVIRONMENT=Production     DOTNET_gcServer=0

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "Billing.Api.dll"]
