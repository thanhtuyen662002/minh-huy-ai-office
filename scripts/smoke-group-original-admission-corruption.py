"""Owned disposable persisted-corruption proof. Never print row bytes."""
from datetime import datetime, timedelta
import hashlib
import re
import uuid


REFUSAL_LINE = 'PASS owned original admission accepted result FIRST remains readable corrupted sidecar refused current epoch three zero admission or provider'
SUMMARY = 'PASS actual original admission four exact companion FK corruption refusals five persisted payload time claim runtime refusals exact restoration healthy reread retained full graphs no model qualification'
PHYSICAL_PREFIX = 'PASS owned exact companion FK prevents sidecar mutation '
COMPANION_FOREIGN_KEY = 'FK_GroupModelAttemptAdmissions_ExactOriginalAdmission'


def verify(*, directory, api, tenant, company, source, batch, sql, run_child, reference, assert_unchanged):
    reference.require_owned(directory, api)
    tenant, company, source, batch = (str(uuid.UUID(x)) for x in (tenant, company, source, batch))
    assert all(uuid.UUID(x).int for x in (tenant, company, source, batch))
    scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND BindingId='{source}' AND BatchId='{batch}'"
    table = 'aioffice.GroupOriginalAdmissionSidecars'
    # Each line stays below sqlcmd's default variable-width column limit.
    # Full bounded payload stays in this private process, never child/log output.
    text = sql(f"""/* owned-admission-corruption:export */
        SELECT CONCAT('L|',DATALENGTH(Sidecar)) FROM {table} WHERE {scope};
        SELECT CONCAT('H|',SidecarSha256) FROM {table} WHERE {scope};
        SELECT CONCAT('T|',CONVERT(varchar(48),CreatedAtUtc,127)) FROM {table} WHERE {scope};
        SELECT CONCAT('C|',LOWER(CONVERT(char(36),OriginalClaimOperationId)),'|',OriginalClaimEpoch) FROM {table} WHERE {scope};
        WITH n AS (SELECT 0 AS i UNION ALL SELECT i+1 FROM n WHERE i<15781)
        SELECT CONCAT('B|',n.i,'|',CONVERT(varchar(128),SUBSTRING(s.Sidecar,n.i*64+1,64),2))
        FROM {table} s CROSS JOIN n WHERE {scope} AND n.i*64<DATALENGTH(s.Sidecar)
          AND DATALENGTH(s.Sidecar) BETWEEN 211 AND 1010000 ORDER BY n.i OPTION(MAXRECURSION 16000);
        """)
    lines = text.splitlines(); assert len(lines) >= 8
    assert re.fullmatch(r'L\|[0-9]{3,7}', lines[0]); length = int(lines[0][2:]); assert 211 <= length <= 1010000
    assert re.fullmatch(r'H\|[0-9A-F]{64}', lines[1]); original_hash = lines[1][2:]
    assert re.fullmatch(r'T\|[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|\+00:00)', lines[2])
    created = lines[2][2:]; assert datetime.fromisoformat(created).utcoffset() == timedelta(0)
    claim = lines[3].split('|'); assert len(claim) == 3 and claim[0] == 'C' and claim[2] == '2'
    claim_operation = str(uuid.UUID(claim[1])); assert uuid.UUID(claim_operation).int
    assert len(lines) == 4 + (length+63)//64
    chunks = []
    for index,line in enumerate(lines[4:]):
        fields = line.split('|'); assert len(fields) == 3 and fields[:2] == ['B',str(index)]
        assert re.fullmatch(r'[0-9A-F]{2,128}', fields[2]) and len(fields[2]) % 2 == 0
        chunk = bytes.fromhex(fields[2]); assert len(chunk) == min(64,length-index*64); chunks.append(chunk)
    original = b''.join(chunks); assert len(original) == length and hashlib.sha256(original).hexdigest().upper() == original_hash
    different_hash = ('B' if original_hash[0] != 'B' else 'C')*64
    payloads = [('altered-magic-bytes', b'Z'+original[1:]),
        ('altered-checksum-bytes',original[:-1]+bytes([original[-1]^1])),('zero-payload',bytes(211))]
    physical = [('row-sha', f"SidecarSha256='{different_hash}'")]
    physical += [(name,f"Sidecar=0x{raw.hex().upper()},SidecarSha256='{hashlib.sha256(raw).hexdigest().upper()}'") for name,raw in payloads]
    # Keep the referenced outer row SHA unchanged in persisted byte cases.
    # They prove altered-byte refusal, not three distinct codec branches.
    cases = [(name,f"Sidecar=0x{raw.hex().upper()}") for name,raw in payloads]
    cases += [('future-created', 'CreatedAtUtc=DATEADD(day,1,CreatedAtUtc)'),
        ('newer-original-claim', f"OriginalClaimOperationId=(SELECT OperationId FROM aioffice.GroupBatchClaimReceipts WHERE {scope} AND Epoch=3),OriginalClaimEpoch=3")]
    assert len(physical) == 4 and len(cases) == 5
    restore = (f"Sidecar=0x{original.hex().upper()},SidecarSha256='{original_hash}',"
        f"OriginalClaimOperationId='{claim_operation}',OriginalClaimEpoch=2,CreatedAtUtc=CONVERT(datetimeoffset,'{created}',127)")
    run_child('admission-original-resume')
    assert_unchanged()
    for name,mutation in physical:
        expected = PHYSICAL_PREFIX + name
        failure = None
        try:
            reference.require_owned(directory, api)
            observed = sql(f"""/* owned-admission-corruption:physical-{name} */
                SET NOCOUNT ON;
                BEGIN TRY
                    BEGIN TRANSACTION;
                    UPDATE {table} SET {mutation} WHERE {scope};
                    IF @@ROWCOUNT<>1 THROW 51000,'Owned corruption row missing',1;
                    ROLLBACK TRANSACTION;
                    THROW 51000,'Owned referenced sidecar mutation was permitted',1;
                END TRY
                BEGIN CATCH
                    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
                    IF ERROR_NUMBER()<>547 OR CHARINDEX(N'"{COMPANION_FOREIGN_KEY}"',ERROR_MESSAGE() COLLATE Latin1_General_100_BIN2)=0 THROW;
                    IF XACT_STATE()<>0 OR @@TRANCOUNT<>0 THROW 51000,'Owned FK rollback incomplete',1;
                    SELECT N'{expected}';
                END CATCH;
                """)
            assert observed.splitlines() == [expected], 'Owned exact companion FK control did not qualify'
        except BaseException as error: failure = error
        finally:
            try:
                assert_unchanged()
                run_child('admission-original-resume')
                assert_unchanged()
            except BaseException as error:
                if failure is None: failure = error
        if failure is not None: raise failure
    for name,mutation in cases:
        failure = None
        try:
            reference.require_owned(directory, api)
            observed = sql(f"/* owned-admission-corruption:{name} */ UPDATE {table} SET {mutation} WHERE {scope}; IF @@ROWCOUNT<>1 THROW 51000,'Owned corruption row missing',1;")
            assert observed == '', 'Owned persisted corruption returned unexpected output'
            run_child('admission-original-refuse')
        except BaseException as error: failure = error
        finally:
            try:
                reference.require_owned(directory, api)
                observed = sql(f"/* owned-admission-corruption:restore */ UPDATE {table} SET {restore} WHERE {scope}; IF @@ROWCOUNT<>1 THROW 51000,'Owned restoration row missing',1;")
                assert observed == '', 'Owned corruption restoration returned unexpected output'
                assert_unchanged()
                run_child('admission-original-resume')
                assert_unchanged()
            except BaseException as error:
                if failure is None: failure = error
        if failure is not None: raise failure
