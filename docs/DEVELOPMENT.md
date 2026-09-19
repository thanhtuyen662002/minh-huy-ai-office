# Development

## Required tools
- .NET SDK 10.0.401 or compatible 10.0 patch.
- Node.js 24.
- npm.
- Docker Desktop / Docker Engine.

## Required local quality gates

Run the same deterministic checks used by GitHub Actions before marking implementation ready:

```bash
npm install --no-audit --no-fund
npm run web:typecheck
npm run web:build

dotnet restore MinhHuy.AIOffice.sln
dotnet format MinhHuy.AIOffice.sln --no-restore --verify-no-changes --verbosity normal
dotnet build MinhHuy.AIOffice.sln --configuration Release --no-restore
dotnet test MinhHuy.AIOffice.sln --configuration Release --no-build --no-restore --logger "console;verbosity=normal"

docker compose --env-file .env.example config --quiet
```

Migration integrity is checked without contacting a live SQL Server. The design-time factory only needs a syntactically valid connection string while EF generates the SQL script:

```bash
export AIOFFICE_DB_CONNECTION='Server=127.0.0.1,1433;Database=AIOfficeCiValidation;User Id=sa;Password=CiValidationOnly_123!;TrustServerCertificate=true;Connect Timeout=1'
dotnet tool restore
dotnet build src/Platform.Persistence/Platform.Persistence.csproj --configuration Release --no-restore
dotnet ef migrations script 0 --project src/Platform.Persistence/Platform.Persistence.csproj --configuration Release --no-build --idempotent --output /tmp/aioffice-migrations.sql
test -s /tmp/aioffice-migrations.sql
grep -q '__EFMigrationsHistory' /tmp/aioffice-migrations.sql
grep -q '20260919114500_InitialPlatformFoundation' /tmp/aioffice-migrations.sql
```

GitHub exposes the component jobs plus the aggregate **Required quality gates** job as machine-readable check results. Governance remains a separate mandatory workflow and must stay green.

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

The repository contains no customer credentials, database connections or AI provider keys. The CI-only connection string above is a non-routable validation value and is not a production secret.
