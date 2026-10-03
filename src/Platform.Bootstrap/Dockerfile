FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Platform.Bootstrap/Platform.Bootstrap.csproj --configuration Release --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "MinhHuy.AIOffice.Platform.Bootstrap.dll"]
