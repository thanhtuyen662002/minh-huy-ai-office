"""Synthetic continuation controls, without real Core/SQL/model execution."""
from datetime import datetime, timezone
import hashlib
import importlib.util
import os
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result); return result


history = module('owned_history_continuation','owned-group-history-fixture.py')
fixture = module('history_base_fixture','owned-group-effect-fixture.py')


class HistoryContinuationTests(unittest.TestCase):
    environment = {'CI':'true','GITHUB_ACTIONS':'true','RUNNER_TEMP':'/owned'}

    def oracle(self, fault=None):
        tenant,company,service,source,account,owner = [str(uuid.uuid4()) for _ in range(6)]
        events = [str(uuid.uuid4()) for _ in range(3)]
        payloads = []; queries = []; code = 'REQ-'+uuid.uuid4().hex.upper()
        def post(payload):
            payloads.append(payload)
            current = len(payloads)
            value = dict(source=dict(tenantId=tenant,companyId=company,sourceBindingId=source),messageId=str(uuid.uuid4()),
                revision=1,committedSequence=current,committedAtUtc=datetime.now(timezone.utc).isoformat(),wasAlreadyCommitted=False)
            if current == 3:
                if fault == 'status': return 503, value
                if fault == 'foreign': value['source']['sourceBindingId'] = str(uuid.uuid4())
                if fault == 'sequence': value['committedSequence'] = True
                if fault == 'revision': value['revision'] = True
                if fault == 'replayed': value['wasAlreadyCommitted'] = True
                if fault == 'extra': value['private'] = 'PRIVATE_SQL'
                if fault == 'time': value['committedAtUtc'] = '2026-10-12T01:00:00'
            return 200, value
        def initial(**kwargs):
            for _ in range(2):
                payload = dict(event=dict(identity=dict(provider='synthetic',accountId='owned-account',groupId='owned-group'),
                    kind=2,messageId='opaque-'+uuid.uuid4().hex,revisionEventId=str(uuid.uuid4()),replyToMessageId=None,
                    occurredAtUtc=datetime.now(timezone.utc).isoformat(),contentSha256='A'*64,isHistoricalBackfill=False),
                    text='initial',isGroup=True,isSelf=False,isKnownReportEcho=False,listenerOwnerId=owner,listenerEpoch=1)
                kwargs['post_event'](payload)
            return source, events[:2]
        def sql(query):
            queries.append(query)
            if query.startswith('SELECT RequestCode'):
                # Shipping notes use one-based ordinals. A prefix-only oracle
                # previously hid a nonexistent ordinal and missing scope.
                self.assertEqual(
                    f"SELECT RequestCode FROM aioffice.GroupCustomerRequests WHERE TenantId='{tenant}' "
                    f"AND CompanyId='{company}' AND BindingId='{source}' AND OriginCandidateOrdinal=1;",
                    query)
                return 'PRIVATE_SQL' if fault == 'code' else code
            if query.startswith('SELECT LOWER(CONVERT(char(36),ConnectorAccountId))'): return account
            if query.startswith('SET NOCOUNT ON;'): return '0' if fault == 'lease' else '1'
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id))'): return events[0] if fault == 'event' else events[2]
            self.fail('Unexpected continuation SQL')
        args = dict(directory=Path('/owned/aioffice-local'),api='http://127.0.0.1:8080',tenant=tenant,company=company,service=service,
            sql=sql,identity_index=lambda *args:'A'*64,enroll_source=lambda *args:None,post_event=post,
            fixture=SimpleNamespace(require_owned=fixture.require_owned,prepare_automatic=initial))
        return args,source,events,payloads,queries,code

    def test_unowned_initialization_denies_before_callbacks_and_configuration(self):
        def forbidden(*args,**kwargs): self.fail('Unowned continuation reached a resource')
        args,_,_,_,_,_ = self.oracle()
        for invalid in ({'CI':'false'},{'GITHUB_ACTIONS':'false'},{'RUNNER_TEMP':'/foreign'}):
            with self.subTest(invalid=invalid),patch.dict(os.environ,{**self.environment,**invalid},clear=True),self.assertRaises(RuntimeError):
                history.prepare(**{**args,'sql':forbidden,'post_event':forbidden,'fixture':SimpleNamespace(require_owned=fixture.require_owned,prepare_automatic=forbidden)})

    def test_exact_one_new_Core_event_uses_original_opaque_reply_and_current_request_code(self):
        args,source,events,payloads,queries,code = self.oracle()
        with patch.dict(os.environ,self.environment,clear=True):
            selected,original,append = history.prepare(**args)
            self.assertEqual((source,events[:2]),(selected,original)); self.assertEqual([],queries)
            self.assertEqual(events[2],append(source))
        self.assertEqual(3,len(payloads)); current = payloads[2]
        self.assertEqual(payloads[0]['event']['messageId'],current['event']['replyToMessageId'])
        self.assertIsNone(payloads[0]['event']['replyToMessageId']); self.assertEqual(2,payloads[0]['event']['kind'])
        self.assertEqual(1,current['event']['kind']); self.assertIn(code,current['text'])
        self.assertEqual(hashlib.sha256(current['text'].encode()).hexdigest().upper(),current['event']['contentSha256'])
        self.assertNotEqual(payloads[0]['event']['messageId'],current['event']['messageId'])
        self.assertIn('ExpiresAtUtc>@now',queries[2]); self.assertIn('AND Epoch=1',queries[2])

    def test_foreign_unowned_and_repeat_continuation_deny_before_any_more_resources(self):
        args,source,_,payloads,queries,_ = self.oracle()
        with patch.dict(os.environ,self.environment,clear=True):
            _,_,append = history.prepare(**args)
            with self.assertRaises(AssertionError): append(str(uuid.uuid4()))
            self.assertEqual([],queries); self.assertEqual(2,len(payloads))
            with patch.dict(os.environ,{'CI':'false'}),self.assertRaises(RuntimeError): append(source)
            self.assertEqual([],queries)
            append(source); count = len(queries)
            with self.assertRaises(AssertionError): append(source)
            self.assertEqual(count,len(queries)); self.assertEqual(3,len(payloads))

    def test_missing_code_or_expired_fixture_lease_prevents_Core_post_and_uncertain_retry(self):
        for fault in ('code','lease'):
            args,source,_,payloads,queries,_ = self.oracle(fault)
            with self.subTest(fault=fault),patch.dict(os.environ,self.environment,clear=True):
                _,_,append = history.prepare(**args)
                with self.assertRaises(AssertionError) as error: append(source)
                self.assertNotIn('PRIVATE_SQL',str(error.exception)); self.assertEqual(2,len(payloads)); count = len(queries)
                with self.assertRaises(AssertionError): append(source)
                self.assertEqual(count,len(queries))

    def test_incomplete_foreign_replayed_or_nonexact_Core_ack_never_qualifies_or_retries(self):
        for fault in ('status','foreign','sequence','revision','replayed','extra','time','event'):
            args,source,_,payloads,queries,_ = self.oracle(fault)
            with self.subTest(fault=fault),patch.dict(os.environ,self.environment,clear=True):
                _,_,append = history.prepare(**args)
                with self.assertRaises((AssertionError,ValueError,TypeError)): append(source)
                count = len(queries)
                with self.assertRaises(AssertionError): append(source)
                self.assertEqual(count,len(queries)); self.assertEqual(3,len(payloads))


if __name__ == '__main__': unittest.main()
