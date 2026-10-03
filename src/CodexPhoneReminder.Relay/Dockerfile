# Build with the repository root as the Docker context:
# docker build -f src/CodexPhoneReminder.Relay/Dockerfile -t codex-phone-relay .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.Config ./NuGet.Config
COPY src/CodexPhoneReminder.Relay/CodexPhoneReminder.Relay.csproj src/CodexPhoneReminder.Relay/
RUN dotnet restore src/CodexPhoneReminder.Relay/CodexPhoneReminder.Relay.csproj

COPY src/CodexPhoneReminder.Relay/ src/CodexPhoneReminder.Relay/
RUN dotnet publish src/CodexPhoneReminder.Relay/CodexPhoneReminder.Relay.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV Relay__StatePath=/app/data/relay-state.json

COPY --from=build /out ./
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data
USER $APP_UID
VOLUME ["/app/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "CodexPhoneReminder.Relay.dll"]
