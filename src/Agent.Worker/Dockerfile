FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/Agent.Worker/Agent.Worker.csproj
RUN dotnet publish src/Agent.Worker/Agent.Worker.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .
USER app

ENTRYPOINT ["dotnet", "MinhHuy.AIOffice.Agent.Worker.dll"]
