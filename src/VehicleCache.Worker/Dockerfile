# Build context: repository root (see devspace.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/VehicleCache.Projection/VehicleCache.Projection.csproj src/VehicleCache.Projection/
COPY src/VehicleCache.Worker/VehicleCache.Worker.csproj src/VehicleCache.Worker/
RUN dotnet restore src/VehicleCache.Worker/VehicleCache.Worker.csproj
COPY src/VehicleCache.Projection/ src/VehicleCache.Projection/
COPY src/VehicleCache.Worker/ src/VehicleCache.Worker/
RUN dotnet publish src/VehicleCache.Worker/VehicleCache.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0.12
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "VehicleCache.Worker.dll"]
