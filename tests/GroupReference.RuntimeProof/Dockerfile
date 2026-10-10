FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish tests/GroupReference.RuntimeProof/GroupReference.RuntimeProof.csproj \
    --configuration Release --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "MinhHuy.AIOffice.GroupReference.RuntimeProof.dll"]
