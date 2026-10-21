# Build context: repository root (see devspace.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/VehicleCache.Projection/VehicleCache.Projection.csproj src/VehicleCache.Projection/
COPY src/VehicleCache.Verifier/VehicleCache.Verifier.csproj src/VehicleCache.Verifier/
RUN dotnet restore src/VehicleCache.Verifier/VehicleCache.Verifier.csproj
COPY src/VehicleCache.Projection/ src/VehicleCache.Projection/
COPY src/VehicleCache.Verifier/ src/VehicleCache.Verifier/
RUN dotnet publish src/VehicleCache.Verifier/VehicleCache.Verifier.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0.12
# Npgsql probes GSSAPI at startup; without this lib the runtime logs a (harmless) libgssapi_krb5 error.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "VehicleCache.Verifier.dll"]
