# Ретранслятор удалённой поддержки NurMarket (2026-09-29).
# Сборка из папки NurRemoteSupport:  docker build -f deploy/Dockerfile -t nurmarket-relay .
# (docker-compose.yml в этой папке делает это сам)

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Server/Server.csproj Server/
RUN dotnet restore Server/Server.csproj
COPY Server/ Server/
RUN dotnet publish Server/Server.csproj -c Release -o /app --no-restore /p:UseAppHost=false \
 && rm -f /app/appsettings.example.json

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
# Папка данных: operators.json, journal.jsonl, token-secret. Владелец — непривилегированный пользователь app.
RUN mkdir -p /data && chown "$APP_UID":"$APP_UID" /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Relay__DataDir=/data \
    DOTNET_gcServer=0 \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Server.dll"]
