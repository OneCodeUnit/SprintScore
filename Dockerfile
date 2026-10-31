# syntax=docker/dockerfile:1

# ---- сборка -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только файл проекта: слой с restore переживает правки кода.
COPY src/SprintScore/SprintScore.csproj src/SprintScore/
RUN dotnet restore src/SprintScore/SprintScore.csproj

COPY . .
RUN dotnet publish src/SprintScore/SprintScore.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish

# ---- рантайм ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl нужен только для HEALTHCHECK — в базовом образе его нет.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Production
# Приложение стоит за обратным прокси: доверяем X-Forwarded-For и X-Forwarded-Proto.
ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

EXPOSE 8080

# APP_UID задан в базовом образе: работаем не от root.
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "SprintScore.dll"]
