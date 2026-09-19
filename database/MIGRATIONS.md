# SQL Server Migrations

The AI Office platform database is versioned through EF Core migrations in `src/Platform.Persistence/Migrations`.

## Rules
1. Never edit an already-deployed historical migration.
2. Create a new migration for every schema change.
3. Default to **EXPAND -> MIGRATE -> CONTRACT** so old/new application versions can overlap.
4. Destructive contract steps happen only after old workers/tasks are drained and compatibility is verified.
5. No migration may embed production/customer secrets.
6. Customer ERP databases are not automatically migrated by the AI Office platform migration pipeline; ERP/customer changes use explicit adapters/change plans.

## Design-time connection
Set a development/test connection through the environment:

```bash
export AIOFFICE_DB_CONNECTION='...'
```

Then run EF commands against the persistence project:

```bash
dotnet ef migrations list --project src/Platform.Persistence/Platform.Persistence.csproj
dotnet ef migrations add <Name> --project src/Platform.Persistence/Platform.Persistence.csproj
dotnet ef database update --project src/Platform.Persistence/Platform.Persistence.csproj
```

The EF migration history table is stored in schema `aioffice`.

## Production
Production migrations are release artifacts and run through the Change/Release Plane. Direct SSMS edits are not the normal deployment path. Emergency manual changes must be reconciled into a migration immediately.
