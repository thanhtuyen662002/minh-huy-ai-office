"""Owned disposable actual-Core edit fixture; never a production listener or provider."""
from datetime import datetime, timezone
import copy
import hashlib
import uuid


def require_utc_timestamp(ack):
    """Refuse malformed Core time without exposing response content in errors."""
    failure = 'Owned invalidation Core timestamp is not available.'
    value = ack.get('committedAtUtc') if type(ack) is dict else None
    if type(value) is not str or not 1 <= len(value) <= 40:
        raise AssertionError(failure) from None
    try:
        committed = datetime.fromisoformat(value.replace('Z', '+00:00'))
    except (ValueError, TypeError, OverflowError):
        raise AssertionError(failure) from None
    if committed.tzinfo is None or committed.utcoffset() != timezone.utc.utcoffset(committed):
        raise AssertionError(failure) from None


def prepare(*, directory, api, tenant, company, service, sql, identity_index, enroll_source, post_event, base_fixture):
    base_fixture.require_owned(directory, api)
    assert all(callable(value) for value in (sql, identity_index, enroll_source, post_event))
    tenant, company, service = (str(uuid.UUID(value)) for value in (tenant, company, service))
    originals = []

    def capture(payload):
        assert len(originals) < 2 and payload['event']['kind'] == 1
        result = post_event(payload)
        require_utc_timestamp(result[1])
        originals.append((copy.deepcopy(payload), copy.deepcopy(result)))
        return result

    source, events = base_fixture.prepare(directory=directory, api=api, tenant=tenant, company=company,
        service=service, sql=sql, identity_index=identity_index, enroll_source=enroll_source,
        post_event=capture, edit_fixture=True)
    assert len(originals) == 2 and len(events) == len(set(events)) == 2
    source = str(uuid.UUID(source))
    owner = str(uuid.UUID(originals[0][0]['listenerOwnerId']))
    assert originals[0][0]['listenerEpoch'] == 1 and originals[1][0]['listenerOwnerId'] == owner
    account = sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings "
        f"WHERE TenantId='{tenant}' AND CompanyId='{company}' AND Id='{source}';")
    assert str(uuid.UUID(account)) == account and uuid.UUID(account).int != 0
    used = False

    def append(source_id):
        nonlocal used
        base_fixture.require_owned(directory, api)
        assert source_id == source and not used
        used = True
        # Existing still-live owned lease only. No new epoch, expired lease
        # resurrection or continuity claim; actual Core checks it independently.
        assert sql("SET NOCOUNT ON; DECLARE @now datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00');"
            " UPDATE aioffice.GroupListenerLeases SET HeartbeatAtUtc=@now,ExpiresAtUtc=DATEADD(second,30,@now)"
            f" WHERE TenantId='{tenant}' AND CompanyId='{company}' AND ConnectorAccountId='{account}'"
            f" AND OwnerId='{owner}' AND Epoch=1 AND HeartbeatAtUtc<=@now AND ExpiresAtUtc>@now; SELECT @@ROWCOUNT;") == '1'
        payload = copy.deepcopy(originals[0][0])
        text = 'owned edited inventory question after immutable cutoff'
        payload['text'] = text
        payload['event'].update(kind=3, revisionEventId=str(uuid.uuid4()),
            occurredAtUtc=datetime.now(timezone.utc).isoformat(),
            contentSha256=hashlib.sha256(text.encode('utf-8')).hexdigest().upper())
        status, ack = post_event(payload)
        assert status == 200 and isinstance(ack, dict) and set(ack) == {
            'source', 'messageId', 'revision', 'committedSequence', 'committedAtUtc', 'wasAlreadyCommitted'}
        assert ack['source'] == {'tenantId': tenant, 'companyId': company, 'sourceBindingId': source}
        assert ack['messageId'] == originals[0][1][1]['messageId']
        assert type(ack['revision']) is int and ack['revision'] == 2
        assert type(ack['committedSequence']) is int and ack['committedSequence'] == 3 and ack['wasAlreadyCommitted'] is False
        require_utc_timestamp(ack)
        return status, ack

    return source, events, append
