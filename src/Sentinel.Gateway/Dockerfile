# syntax=docker/dockerfile:1
# Build context: repository root.   docker build -f src/Sentinel.Gateway/Dockerfile -t sentinel-gateway .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: only re-runs when a project file or a package version changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Sentinel.Domain/Sentinel.Domain.csproj src/Sentinel.Domain/
COPY src/Sentinel.Guardrails/Sentinel.Guardrails.csproj src/Sentinel.Guardrails/
COPY src/Sentinel.Application/Sentinel.Application.csproj src/Sentinel.Application/
COPY src/Sentinel.Infrastructure/Sentinel.Infrastructure.csproj src/Sentinel.Infrastructure/
COPY src/Sentinel.Migrations.SqlServer/Sentinel.Migrations.SqlServer.csproj src/Sentinel.Migrations.SqlServer/
COPY src/Sentinel.Gateway/Sentinel.Gateway.csproj src/Sentinel.Gateway/
RUN dotnet restore src/Sentinel.Gateway/Sentinel.Gateway.csproj

COPY src/ src/
RUN dotnet publish src/Sentinel.Gateway/Sentinel.Gateway.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# Chiseled "extra": distroless Ubuntu (no shell, no package manager, non-root user) plus ICU, which SqlClient needs.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sentinel.Gateway.dll"]
