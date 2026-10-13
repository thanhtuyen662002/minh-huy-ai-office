"""Owned actual Core continuation after shipping historical notes commit."""
from datetime import datetime, timezone
import copy
import hashlib
import re
import uuid


def prepare(*, directory, api, tenant, company, service, sql, identity_index, enroll_source, post_event, fixture):
    fixture.require_owned(directory, api)
    assert all(callable(value) for value in (sql, identity_index, enroll_source, post_event))
    first = None

    def observe(payload):
        nonlocal first
        response = post_event(payload)
        if first is None:
            first = copy.deepcopy(payload)
        return response

    source, events = fixture.prepare_automatic(directory=directory, api=api, tenant=tenant, company=company, service=service,
        sql=sql, identity_index=identity_index, enroll_source=enroll_source, post_event=observe)
    assert first is not None
    tenant, company, source = (str(uuid.UUID(value)) for value in (tenant, company, source))
    owner = str(uuid.UUID(first['listenerOwnerId']))
    scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND BindingId='{source}'"
    used = False

    def append(source_id):
        nonlocal used
        fixture.require_owned(directory, api)
        assert type(source_id) is str and source_id == source and not used, 'Owned history continuation scope or replay refused'
        used = True  # Uncertain actual Core publication is never repeated.
        code = sql(f'SELECT RequestCode FROM aioffice.GroupCustomerRequests WHERE {scope} AND OriginCandidateOrdinal=0;')
        assert type(code) is str and re.fullmatch(r'REQ-[0-9A-F]{32}', code), 'Owned history request code unavailable'
        account = sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE TenantId='{tenant}' AND CompanyId='{company}' AND Id='{source}';")
        assert type(account) is str and str(uuid.UUID(account)) == account and uuid.UUID(account).int != 0
        # Narrow synthetic fixture lease renewal, without a listener command,
        # changed epoch/owner, production account or new continuity claim.
        assert sql("SET NOCOUNT ON; DECLARE @now datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00');"
            " UPDATE aioffice.GroupListenerLeases SET HeartbeatAtUtc=@now,ExpiresAtUtc=DATEADD(second,30,@now)"
            f" WHERE TenantId='{tenant}' AND CompanyId='{company}' AND ConnectorAccountId='{account}'"
            f" AND OwnerId='{owner}' AND Epoch=1 AND HeartbeatAtUtc<=@now AND ExpiresAtUtc>@now; SELECT @@ROWCOUNT;") == '1'
        text = 'Theo dõi '+code+': tra cứu tồn kho, cần bổ sung kho nào?'
        payload = copy.deepcopy(first)
        payload['text'] = text
        payload['event'].update(kind=1,messageId=str(uuid.uuid4()),revisionEventId=str(uuid.uuid4()),
            replyToMessageId=first['event']['messageId'],occurredAtUtc=datetime.now(timezone.utc).isoformat(),
            contentSha256=hashlib.sha256(text.encode('utf-8')).hexdigest().upper())
        status, value = post_event(payload)
        assert status == 200 and isinstance(value, dict) and set(value) == {
            'source','messageId','revision','committedSequence','committedAtUtc','wasAlreadyCommitted'}, 'Owned history Core acknowledgement failed'
        assert value['source'] == dict(tenantId=tenant,companyId=company,sourceBindingId=source)
        assert type(value['messageId']) is str and str(uuid.UUID(value['messageId'])) == value['messageId'] and uuid.UUID(value['messageId']).int != 0
        assert type(value['revision']) is int and value['revision'] == 1
        assert type(value['committedSequence']) is int and value['committedSequence'] == 3 and value['wasAlreadyCommitted'] is False
        committed = datetime.fromisoformat(value['committedAtUtc'].replace('Z','+00:00'))
        assert committed.utcoffset() == timezone.utc.utcoffset(committed)
        event = sql(f'SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupIngressOutbox WHERE {scope} AND CommittedSequence=3;')
        assert type(event) is str and str(uuid.UUID(event)) == event and uuid.UUID(event).int != 0 and event not in events
        return event

    return source, events, append
