# Development

## Required tools
- .NET SDK 10.0.401 or compatible 10.0 patch.
- Node.js 24.
- npm.
- Docker Desktop / Docker Engine will be required by the infrastructure issue.

## Build
```bash
npm install
npm run web:build
dotnet restore MinhHuy.AIOffice.sln
dotnet build MinhHuy.AIOffice.sln --configuration Release --no-restore
dotnet test MinhHuy.AIOffice.sln --configuration Release --no-build --no-restore
```

## Run web
```bash
npm run web:dev
```

## Run API
```bash
dotnet run --project src/Core.Api/Core.Api.csproj
```

## Run worker
```bash
dotnet run --project src/Agent.Worker/Agent.Worker.csproj
```

The initial scaffold intentionally contains no customer credentials, database connections or AI provider keys.
