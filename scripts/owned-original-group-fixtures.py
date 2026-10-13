"""Closed identities for four owned original SQL fixture roles, never production."""
import importlib.util
from pathlib import Path
import uuid


# Enrollment slots, frame ordinals, exact labels and original receipt shapes.
ROLES = {
    4: (1, 'Owned original automatic fixture v1', 1, 4),
    5: (2, 'Owned original no-work fixture v1', 2, 0),
    6: (3, 'Owned original attention fixture v1', 3, 1),
    8: (4, 'Owned original raw-history fixture v1', 2, 0),
}


def canonical(value):
    assert type(value) is str and str(uuid.UUID(value)) == value and uuid.UUID(value).int != 0
    return value


def enroll(*, directory, api, tenant, company, source, slot, sql):
    spec = importlib.util.spec_from_file_location('original_fixture_boundary', Path(__file__).with_name('owned-group-effect-fixture.py'))
    boundary = importlib.util.module_from_spec(spec); spec.loader.exec_module(boundary)
    boundary.require_owned(directory, api)  # Before scope/callback/configuration.
    assert type(slot) is int and slot in (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 16)
    if slot not in ROLES:
        return
    tenant, company, source = (canonical(value) for value in (tenant, company, source))
    assert callable(sql)
    scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND BindingId='{source}'"
    label = ROLES[slot][1]
    # Test-only binding identity is set exactly once before any source event,
    # allocation or claim. No version/authority/lease/customer row is changed.
    query = ("SET NOCOUNT ON; UPDATE aioffice.GroupBindings SET DisplayName=N'"+label+"' "
        f"WHERE TenantId='{tenant}' AND CompanyId='{company}' AND Id='{source}' "
        "AND Provider=N'synthetic' AND Role=1 AND IsEnabled=1 AND Version=1 AND DeletionGeneration=0 "
        "AND DisplayName=N'Owned clean SQL effect fixture' "
        + ''.join(f"AND NOT EXISTS(SELECT 1 FROM aioffice.{table} WHERE {scope}) "
            for table in ('GroupMessages','GroupIngressReceipts','GroupSourceStates','GroupBatchAllocations','GroupBatchClaimReceipts'))
        + "; SELECT @@ROWCOUNT;")
    assert sql(query) == '1', 'Owned original fixture enrollment unavailable'


def frames_query(tenant, company, *, require_terminal=False):
    tenant, company = (canonical(value) for value in (tenant, company))
    assert type(require_terminal) is bool
    # A fixed role ordinal exposes a duplicate role balanced by a missing one
    # to the unchanged strict24-frame parser. ROW_NUMBER would hide that case.
    ordinal = "CASE b.DisplayName COLLATE Latin1_General_100_BIN2 "+' '.join(
        f"WHEN N'{label}' THEN {number}" for number,label,_,_ in ROLES.values())+" END"
    signatures = ' OR '.join(
        f"(b.DisplayName COLLATE Latin1_General_100_BIN2=N'{label}' AND w.Outcome={outcome} AND w.NoteCount={count})"
        for _,label,outcome,count in ROLES.values())
    terminal = ("JOIN aioffice.GroupBatchTerminalReceipts t ON t.TenantId=w.TenantId AND t.CompanyId=w.CompanyId "
        "AND t.BindingId=w.BindingId AND t.BatchId=w.BatchId " if require_terminal else '')
    return ("SELECT CONVERT(varchar(40),CONCAT(f.fixtureOrdinal,':',v.fieldOrdinal,':',LOWER(CONVERT(char(36),v.id)))) "
        f"FROM (SELECT TOP(5) {ordinal} AS fixtureOrdinal,"
        "w.TenantId AS tenantId,w.CompanyId AS companyId,a.ServiceId AS serviceId,w.BindingId AS sourceId,"
        "a.OperationId AS eventId,a.Id AS batchId "
        "FROM aioffice.GroupWorkCommitReceipts w JOIN aioffice.GroupBatchAllocations a ON a.TenantId=w.TenantId "
        "AND a.CompanyId=w.CompanyId AND a.BindingId=w.BindingId AND a.Id=w.BatchId "
        "JOIN aioffice.GroupBindings b ON b.TenantId=w.TenantId AND b.CompanyId=w.CompanyId AND b.Id=w.BindingId "
        + terminal
        + f"WHERE w.TenantId='{tenant}' AND w.CompanyId='{company}' AND w.DependencyManifestVersion=1 "
        "AND b.Provider=N'synthetic' AND b.Role=1 AND b.IsEnabled=1 AND b.Version=1 AND b.DeletionGeneration=0 "
        "AND DATALENGTH(w.DependencyManifest)=274 AND w.SelectedMessageCount=2 AND SUBSTRING(w.DependencyManifest,161,2)=0x0200 "
        f"AND ({signatures}) ORDER BY fixtureOrdinal,w.BindingId) f "
        "CROSS APPLY (VALUES (1,f.tenantId),(2,f.companyId),(3,f.serviceId),(4,f.sourceId),(5,f.eventId),(6,f.batchId)) "
        "v(fieldOrdinal,id) ORDER BY f.fixtureOrdinal,v.fieldOrdinal,f.sourceId;")
