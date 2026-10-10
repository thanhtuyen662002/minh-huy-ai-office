"""Separate no-gap SQL effect fixture; never qualifies listener recovery."""
from datetime import datetime, timezone
import hashlib
import os
from pathlib import Path
import re
import uuid


def require_owned(directory, api):
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080"):
        raise RuntimeError("Clean group effect fixture requires the owned disposable GitHub CI fixture.")


def prepare(*, directory, api, tenant, company, service, sql, identity_index, enroll_source, post_event):
    require_owned(directory, api)  # Before callbacks, configuration or SQL.
    assert all(callable(value) for value in (sql, identity_index, enroll_source, post_event))
    tenant, company, service = (str(uuid.UUID(value)) for value in (tenant, company, service))
    assert all(uuid.UUID(value).int != 0 for value in (tenant, company, service))
    source, account, listener = (str(uuid.uuid4()) for _ in range(3))
    external_account = "owned-effect-account-" + uuid.uuid4().hex
    external_group = "owned-effect-group-" + uuid.uuid4().hex
    scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND BindingId='{source}'"
    account_scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND ConnectorAccountId='{account}'"
    hashes = [identity_index('synthetic', external_account, 'account-registry'),
        identity_index('synthetic', external_account, external_group), identity_index('synthetic', 'physical-group-registry', external_group)]
    assert all(isinstance(value, str) and re.fullmatch(r"[0-9A-F]{64}", value) for value in hashes)
    # Generated closed identifiers only. This is test-only operator enrollment.
    sql("SET XACT_ABORT ON; BEGIN TRANSACTION;"
        " INSERT aioffice.GroupConnectorAccounts(TenantId,CompanyId,Id,Provider,ExternalAccountId,IdentityHash,PackageVersion,GitCommit,QualificationJson,Version,IsEnabled)"
        f" VALUES('{tenant}','{company}','{account}','synthetic',N'{external_account}',"
        f"'{hashes[0]}','owned-fixture','{'0'*40}',N'{{\"environment\":1,\"observations\":[]}}',1,1);"
        " INSERT aioffice.GroupBindings(TenantId,CompanyId,Id,ConnectorAccountId,Role,Provider,ExternalAccountId,ExternalGroupId,IdentityHash,PhysicalGroupHash,DisplayName,Version,DeletionGeneration,IsEnabled)"
        f" VALUES('{tenant}','{company}','{source}','{account}',1,'synthetic',N'{external_account}',N'{external_group}',"
        f"'{hashes[1]}','{hashes[2]}',N'Owned clean SQL effect fixture',1,0,1);"
        " INSERT aioffice.GroupServiceGrants(TenantId,CompanyId,ServiceId,BindingId,Capability,Version,IsEnabled)"
        f" VALUES('{tenant}','{company}','{service}','{source}',1,1,1),('{tenant}','{company}','{service}','{source}',2,1,1); COMMIT;")
    enroll_source(source)
    # Seed ONLY after configured Core is ready. No listener command, command
    # receipt or fresh-listener continuity claim; Acquire would record a gap.
    sql("DECLARE @now datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00');"
        " INSERT aioffice.GroupListenerLeases(TenantId,CompanyId,ConnectorAccountId,OwnerId,Epoch,HeartbeatAtUtc,ExpiresAtUtc)"
        f" VALUES('{tenant}','{company}','{account}','{listener}',1,@now,DATEADD(second,30,@now));")
    text = "owned native spool 😀\uFEFF "  # Retain the existing source vector.
    receipts = []
    for sequence in (1, 2):
        payload = {"event": {"identity": {"provider": "synthetic", "accountId": external_account, "groupId": external_group},
            "messageId": str(uuid.uuid4()), "revisionEventId": str(uuid.uuid4()), "senderId": "owned-effect-sender",
            "replyToMessageId": None, "kind": 1, "occurredAtUtc": datetime.now(timezone.utc).isoformat(),
            "contentSha256": hashlib.sha256(text.encode("utf-8")).hexdigest().upper(), "isHistoricalBackfill": False},
            "text": text, "isGroup": True, "isSelf": False, "isKnownReportEcho": False,
            "listenerOwnerId": listener, "listenerEpoch": 1}
        status, value = post_event(payload)
        assert status == 200 and isinstance(value, dict) and set(value) == {
            "source", "messageId", "revision", "committedSequence", "committedAtUtc", "wasAlreadyCommitted"}, "Owned clean effect Core ACK failed"
        assert value["source"] == {"tenantId": tenant, "companyId": company, "sourceBindingId": source}
        assert str(uuid.UUID(value["messageId"])) == value["messageId"] and uuid.UUID(value["messageId"]).int != 0
        assert type(value["revision"]) is int and value["revision"] == 1
        assert type(value["committedSequence"]) is int and value["committedSequence"] == sequence
        assert value["wasAlreadyCommitted"] is False
        datetime.fromisoformat(value["committedAtUtc"].replace("Z", "+00:00"))
        receipts.append(value)
    assert receipts[0]["messageId"] != receipts[1]["messageId"]
    assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupCoverageGaps WHERE {scope}),N'|',"
        f"(SELECT COUNT(*) FROM aioffice.GroupAccountCoverageGaps WHERE {account_scope}),N'|',"
        f"(SELECT COUNT(*) FROM aioffice.GroupListenerCommandReceipts WHERE {account_scope}));") == "0|0|0"
    events = sql(f"SELECT CONVERT(varchar(36),Id) FROM aioffice.GroupIngressOutbox WHERE {scope} ORDER BY CommittedSequence;").splitlines()
    events = [str(uuid.UUID(value.strip())) for value in events]
    assert len(events) == 2 and len(set(events)) == 2
    return source, events


def prepare_automatic(*, directory, api, tenant, company, service, sql, identity_index, enroll_source, post_event):
    # A new separately owned scope. Reuse enrollment/ACK/lease/gap checks while
    # replacing only the two synthetic inputs for the combined-writer proof.
    require_owned(directory, api)
    assert callable(post_event)
    sent = 0
    def media_event(payload):
        nonlocal sent
        assert sent < 2 and payload["event"]["kind"] == 1
        text = "Tra cứu tồn kho 😀\uFEFF " if sent == 0 else "Password=OWNED_AUTOMATIC_PRIVATE_SENTINEL"
        sent += 1
        event = dict(payload["event"])
        event["kind"] = 2  # Actual GroupSourceEventKind.Media, not Edit.
        event["contentSha256"] = hashlib.sha256(text.encode("utf-8")).hexdigest().upper()
        return post_event({**payload, "event": event, "text": text})
    result = prepare(directory=directory, api=api, tenant=tenant, company=company, service=service, sql=sql,
        identity_index=identity_index, enroll_source=enroll_source, post_event=media_event)
    assert sent == 2
    return result


def prepare_no_work(*, directory, api, tenant, company, service, sql, identity_index, enroll_source, post_event):
    # A distinct scope with two empty plain texts, which requires no model.
    require_owned(directory, api)
    assert callable(post_event)
    sent = 0
    def empty_event(payload):
        nonlocal sent
        assert sent < 2 and payload["event"]["kind"] == 1
        sent += 1
        event = dict(payload["event"])
        event["contentSha256"] = hashlib.sha256(b"").hexdigest().upper()
        return post_event({**payload, "event": event, "text": ""})
    result = prepare(directory=directory, api=api, tenant=tenant, company=company, service=service, sql=sql,
        identity_index=identity_index, enroll_source=enroll_source, post_event=empty_event)
    assert sent == 2
    return result
