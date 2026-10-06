-- OPERATOR ONLY. Review one exact tenant/company/reference before execution.
-- Use an elevated installation identity, never the application runtime login.
-- This template is inert until all placeholders and expected version are filled.
-- Supply only a canonical secretref locator; credentials never belong here.
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @TenantId uniqueidentifier = NULL;
DECLARE @CompanyId uniqueidentifier = NULL;
DECLARE @GrantId uniqueidentifier = NULL;
DECLARE @CanonicalReference nvarchar(512) = NULL;
DECLARE @Label nvarchar(128) = NULL;
DECLARE @Action nvarchar(32) = N'create-disabled'; -- create-disabled | enable | revoke
DECLARE @ExpectedVersion bigint = NULL; -- required for enable/revoke

IF @TenantId IS NULL OR @CompanyId IS NULL OR @GrantId IS NULL
    OR @CanonicalReference IS NULL OR @Label IS NULL OR LEN(@Label)=0
    THROW 51000, 'Explicit reviewed binding parameters are required.', 1;
-- Canonicalize with SecretReference.Parse before supplying this parameter.
-- Conservative SQL checks supplement (and do not replace) that review.
IF @CanonicalReference COLLATE Latin1_General_100_BIN2 NOT LIKE N'secretref://%/%'
    OR @CanonicalReference LIKE N'%[?@#]%' OR @CanonicalReference LIKE N'% %'
    OR @CanonicalReference LIKE N'%' + NCHAR(9) + N'%'
    OR @CanonicalReference LIKE N'%' + NCHAR(10) + N'%'
    OR @CanonicalReference LIKE N'%' + NCHAR(13) + N'%'
    OR LOWER(@CanonicalReference) = N'secretref://env/aioffice_db_connection'
    THROW 51000, 'A reviewed canonical customer reference is required.', 1;
IF @Action NOT IN (N'create-disabled',N'enable',N'revoke')
    THROW 51000, 'Unsupported explicit operator action.', 1;

BEGIN TRANSACTION;
IF NOT EXISTS (SELECT 1 FROM aioffice.Companies WITH (UPDLOCK,HOLDLOCK)
    WHERE TenantId=@TenantId AND Id=@CompanyId)
    THROW 51000, 'Reviewed company does not exist.', 1;
IF @Action=N'create-disabled'
BEGIN
    -- Replaying creation must never resurrect a revoked/deleted grant implicitly.
    IF EXISTS (SELECT 1 FROM aioffice.DataSourceSecretBindings WITH (UPDLOCK,HOLDLOCK)
        WHERE TenantId=@TenantId AND CompanyId=@CompanyId
          AND (Id=@GrantId OR CanonicalReference=@CanonicalReference COLLATE Latin1_General_100_BIN2))
        THROW 51000, 'Binding already exists; review its identity and version.', 1;
    INSERT aioffice.DataSourceSecretBindings
        (TenantId,CompanyId,Id,CanonicalReference,Label,IsEnabled,Version)
        VALUES (@TenantId,@CompanyId,@GrantId,@CanonicalReference,@Label,0,1);
END
ELSE
BEGIN
    IF @ExpectedVersion IS NULL OR @ExpectedVersion<1
        THROW 51000, 'Explicit expected binding version is required.', 1;
    UPDATE aioffice.DataSourceSecretBindings
       SET IsEnabled=CASE WHEN @Action=N'enable' THEN 1 ELSE 0 END, Version=Version+1, Label=@Label
     WHERE TenantId=@TenantId AND CompanyId=@CompanyId AND Id=@GrantId
       AND CanonicalReference=@CanonicalReference COLLATE Latin1_General_100_BIN2
       AND Version=@ExpectedVersion;
    IF @@ROWCOUNT<>1 THROW 51000, 'Binding identity or version changed; review again.', 1;
END
COMMIT TRANSACTION;
SELECT N'Explicit binding action completed.' AS Result;
