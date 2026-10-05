# Multi-stage build for RecordFlow (Linux container).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY RecordFlow.sln ./
COPY src/RecordFlow.Core/RecordFlow.Core.csproj src/RecordFlow.Core/
COPY src/RecordFlow.Infrastructure/RecordFlow.Infrastructure.csproj src/RecordFlow.Infrastructure/
COPY src/RecordFlow.Web/RecordFlow.Web.csproj src/RecordFlow.Web/
RUN dotnet restore src/RecordFlow.Web/RecordFlow.Web.csproj
COPY src/ src/
RUN dotnet publish src/RecordFlow.Web/RecordFlow.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Run as the image's built-in non-root user; TLS is terminated by the load balancer / ingress.
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# Point your orchestrator's liveness/readiness probe at GET /health (checks the database connection).
ENTRYPOINT ["dotnet", "RecordFlow.Web.dll"]
