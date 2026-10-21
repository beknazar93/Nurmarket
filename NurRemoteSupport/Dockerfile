# 2026-10-16: ретранслятор удалённой помощи NurMarket (NurRemoteSupport). Сборка из папки NurRemoteSupport:
#   docker compose up -d --build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Server/NurRemoteSupport.Server.csproj Server/
RUN dotnet restore Server/NurRemoteSupport.Server.csproj
COPY Server/ Server/
RUN dotnet publish Server/NurRemoteSupport.Server.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Внутри контейнера слушаем все адреса; наружу порт открывает docker-compose только на 127.0.0.1 (дальше — nginx/Caddy).
ENV BIND=0.0.0.0 \
    PORT=8080 \
    ASPNETCORE_HTTP_PORTS=
EXPOSE 8080
# Пользователь без прав root (есть в образах .NET 8).
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "NurRemoteSupport.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "NurRemoteSupport.Server.dll"]
