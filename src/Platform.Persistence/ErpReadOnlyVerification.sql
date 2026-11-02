SELECT CASE WHEN
          (CONVERT(int,SERVERPROPERTY(N'ProductMajorVersion')) BETWEEN 15 AND 17)
          AND (CONVERT(int,SERVERPROPERTY(N'IsHadrEnabled'))=0)
          AND (DB_ID()>4)
          AND (USER_NAME()<>N'dbo')
          AND (ISNULL(IS_SRVROLEMEMBER(N'sysadmin'),1)=0)
          AND (ISNULL(IS_ROLEMEMBER(N'db_owner'),1)=0)
          AND (NOT EXISTS (SELECT 1 FROM sys.server_principals p JOIN sys.login_token t ON t.principal_id=p.principal_id
  WHERE p.is_fixed_role=1 AND p.name<>N'public'))
          AND (NOT EXISTS (SELECT 1 FROM sys.database_principals p JOIN sys.user_token t ON t.principal_id=p.principal_id
  WHERE p.is_fixed_role=1 AND p.name<>N'public'))
          AND (HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW ANY DEFINITION')=1
  AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'VIEW DEFINITION')=1
  AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'CONNECT')=1
  AND EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'DATABASE') WHERE permission_name=N'VIEW DEFINITION')
  AND EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'SERVER') WHERE permission_name=N'CONNECT SQL'))
          AND (NOT EXISTS (SELECT 1 FROM sys.database_permissions p JOIN sys.user_token t ON t.principal_id=p.grantee_principal_id
  WHERE p.state='D' AND p.permission_name IN (N'VIEW DEFINITION',N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION'))
AND NOT EXISTS (SELECT 1 FROM sys.server_permissions p JOIN sys.login_token t ON t.principal_id=p.grantee_principal_id
  WHERE p.state='D' AND p.permission_name IN
    (N'VIEW DEFINITION',N'VIEW ANY DEFINITION',N'VIEW ANY SECURITY DEFINITION',N'VIEW ANY PERFORMANCE DEFINITION')))
          AND (NOT EXISTS (SELECT 1 FROM sys.database_permissions p JOIN sys.user_token t ON t.principal_id=p.grantee_principal_id WHERE p.state='W')
AND NOT EXISTS (SELECT 1 FROM sys.server_permissions p JOIN sys.login_token t ON t.principal_id=p.grantee_principal_id WHERE p.state='W'))
          AND (NOT EXISTS (SELECT 1 FROM sys.database_permissions p JOIN sys.user_token t ON t.principal_id=p.grantee_principal_id
  WHERE p.state='G' AND p.class NOT IN (0,1,3)))
          AND (NOT EXISTS (SELECT 1 FROM sys.server_permissions p JOIN sys.login_token t ON t.principal_id=p.grantee_principal_id
  WHERE p.state='G' AND p.class<>100
    AND NOT (p.class=105 AND p.permission_name=N'CONNECT'
      AND EXISTS (SELECT 1 FROM sys.endpoints e WHERE e.endpoint_id=p.major_id AND e.type=2))))
          AND (NOT EXISTS (SELECT 1 FROM sys.server_principals p JOIN sys.login_token t ON t.principal_id=p.owning_principal_id)
AND NOT EXISTS (SELECT 1 FROM sys.endpoints e JOIN sys.login_token t ON t.principal_id=e.principal_id)
AND NOT EXISTS (SELECT 1 FROM (
  SELECT owning_principal_id AS owner_id FROM sys.database_principals
  UNION ALL SELECT principal_id FROM sys.types
  UNION ALL SELECT principal_id FROM sys.assemblies
  UNION ALL SELECT principal_id FROM sys.xml_schema_collections
  UNION ALL SELECT principal_id FROM sys.service_message_types
  UNION ALL SELECT principal_id FROM sys.service_contracts
  UNION ALL SELECT principal_id FROM sys.services
  UNION ALL SELECT principal_id FROM sys.remote_service_bindings
  UNION ALL SELECT principal_id FROM sys.routes
  UNION ALL SELECT principal_id FROM sys.fulltext_catalogs
  UNION ALL SELECT principal_id FROM sys.symmetric_keys
  UNION ALL SELECT principal_id FROM sys.certificates
  UNION ALL SELECT principal_id FROM sys.asymmetric_keys
  UNION ALL SELECT principal_id FROM sys.fulltext_stoplists
  UNION ALL SELECT principal_id FROM sys.registered_search_property_lists
  UNION ALL SELECT principal_id FROM sys.external_languages
  UNION ALL SELECT principal_id FROM sys.external_libraries
) o JOIN sys.user_token t ON t.principal_id=o.owner_id)
AND NOT EXISTS (SELECT 1 FROM sys.database_scoped_credentials c
  WHERE ISNULL(HAS_PERMS_BY_NAME(c.name,N'DATABASE SCOPED CREDENTIAL',N'CONTROL'),1)<>0))
          AND (NOT EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'SERVER') WHERE permission_name NOT IN (N'CONNECT SQL',N'VIEW ANY DATABASE',N'VIEW ANY DEFINITION',N'VIEW ANY SECURITY DEFINITION',N'VIEW ANY PERFORMANCE DEFINITION',N'VIEW ANY CRYPTOGRAPHICALLY SECURED DEFINITION')))
          AND (NOT EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'DATABASE') WHERE permission_name NOT IN (N'CONNECT',N'SELECT',N'VIEW DEFINITION',N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION',N'VIEW CRYPTOGRAPHICALLY SECURED DEFINITION',N'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION',N'VIEW ANY COLUMN MASTER KEY DEFINITION')))
          AND (NOT EXISTS (SELECT 1 FROM sys.server_principals p
  WHERE p.type IN ('S','U','G','E','X') AND p.principal_id<>SUSER_ID()
    AND ISNULL(HAS_PERMS_BY_NAME(p.name,N'LOGIN',N'IMPERSONATE'),1)<>0)
AND NOT EXISTS (SELECT 1 FROM sys.database_principals p
  WHERE p.type IN ('S','U','G','E','X') AND p.principal_id<>USER_ID()
    AND ISNULL(HAS_PERMS_BY_NAME(p.name,N'USER',N'IMPERSONATE'),1)<>0))
          AND (NOT EXISTS (SELECT 1 FROM sys.database_principals p WHERE p.type='R'
  AND (ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'ALTER'),1)<>0
    OR ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'CONTROL'),1)<>0
    OR ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'TAKE OWNERSHIP'),1)<>0)))
          AND (NOT EXISTS (SELECT 1 FROM sys.schemas s WHERE ISNULL(HAS_PERMS_BY_NAME(s.name,N'SCHEMA',N'VIEW DEFINITION'),0)<>1))
          AND (NOT EXISTS (SELECT 1 FROM sys.schemas s CROSS APPLY sys.fn_my_permissions(s.name,N'SCHEMA') p
  WHERE p.permission_name NOT IN (N'SELECT',N'VIEW DEFINITION',N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION')))
          AND (NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0
  AND o.type IN ('U','V','P','PC','FN','FS','FT','AF','TF','IF','SN','SO','SQ')
  AND ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),N'OBJECT',N'VIEW DEFINITION'),0)<>1))
          AND (NOT EXISTS (SELECT 1 FROM sys.objects o CROSS APPLY sys.fn_my_permissions(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),N'OBJECT') p
  WHERE o.is_ms_shipped=0 AND o.type IN ('U','V','P','PC','FN','FS','FT','AF','TF','IF','SN','SO','SQ')
    AND p.permission_name NOT IN (N'SELECT',N'VIEW DEFINITION',N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION')
    AND NOT (o.type IN ('FN','IF','TF') AND p.permission_name IN (N'EXECUTE',N'REFERENCES'))))
          AND (NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id
  WHERE o.is_ms_shipped=0 AND ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0))
          AND (NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0 AND o.type IN ('P','PC','FS','FT','AF')
  AND (ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),N'OBJECT',N'EXECUTE'),1)<>0
    OR ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),N'OBJECT',N'SELECT'),1)<>0)))
          AND (NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0 AND o.type IN ('FS','FT','AF')))
          AND (NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0 AND o.type IN ('X','RF')))
          AND (NOT EXISTS (SELECT 1 FROM sys.synonyms))
          AND (NOT EXISTS (SELECT 1 FROM sys.sql_expression_dependencies d
  LEFT JOIN sys.objects r ON d.referencing_class=1 AND r.object_id=d.referencing_id
  WHERE d.referencing_class<>12 AND ISNULL(r.type,'') NOT IN ('P','PC','TR','TA','X','RF')
    AND (d.referenced_server_name IS NOT NULL OR d.referenced_database_name IS NOT NULL
      OR d.is_caller_dependent=1 OR d.is_ambiguous=1
      OR (d.referenced_class=1 AND d.referenced_id IS NULL))))
          AND (NOT EXISTS (SELECT 1 FROM sys.sql_modules m JOIN sys.objects o ON o.object_id=m.object_id
  WHERE o.is_ms_shipped=0 AND o.type IN ('V','FN','IF','TF')
    AND (m.definition IS NULL
      OR m.definition COLLATE Latin1_General_100_CI_AS LIKE N'%OPENQUERY%'
      OR m.definition COLLATE Latin1_General_100_CI_AS LIKE N'%OPENROWSET%'
      OR m.definition COLLATE Latin1_General_100_CI_AS LIKE N'%OPENDATASOURCE%')))
          AND (NOT EXISTS (SELECT 1 FROM sys.external_tables))
          AND (NOT EXISTS (SELECT 1 FROM sys.assemblies WHERE is_user_defined=1) AND NOT EXISTS (SELECT 1 FROM sys.types WHERE is_user_defined=1 AND is_assembly_type=1))
          AND (NOT EXISTS (SELECT 1 FROM sys.servers WHERE is_linked=1))
          THEN 1 ELSE 0 END;