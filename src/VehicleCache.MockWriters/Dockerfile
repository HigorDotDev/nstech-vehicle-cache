# Build context: repository root (see devspace.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/VehicleCache.MockWriters/VehicleCache.MockWriters.csproj src/VehicleCache.MockWriters/
RUN dotnet restore src/VehicleCache.MockWriters/VehicleCache.MockWriters.csproj
COPY src/VehicleCache.MockWriters/ src/VehicleCache.MockWriters/
RUN dotnet publish src/VehicleCache.MockWriters/VehicleCache.MockWriters.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0.12
WORKDIR /app
COPY --from=build /app .
COPY scenarios/ scenarios/
USER $APP_UID
ENTRYPOINT ["dotnet", "VehicleCache.MockWriters.dll"]
