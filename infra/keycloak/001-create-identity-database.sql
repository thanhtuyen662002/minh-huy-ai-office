-- DBA-reviewed initial provisioning only. Run against master, before Keycloak starts.
-- Keycloak owns/version-migrates its tables; platform EF migrations never target this DB.
USE [master];
GO
IF DB_ID(N'AIOffice_Identity') IS NULL
    CREATE DATABASE [AIOffice_Identity] COLLATE Latin1_General_100_CI_AS_SC_UTF8;
GO
ALTER DATABASE [AIOffice_Identity] SET READ_COMMITTED_SNAPSHOT ON;
GO
-- Provision a dedicated SQL login through the approved secret manager, then map it
-- ONLY to AIOffice_Identity. Never use sa or the platform/ERP runtime login.
