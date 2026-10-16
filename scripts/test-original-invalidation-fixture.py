"""Actual fixture/enrollment functions with inert ports only; no SQL/Core qualification."""
import ast
import copy
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import traceback
import unittest
import uuid


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module


fixture = load('invalidation_fixture_control', 'owned-group-invalidation-fixture.py')
enrollment = load('first_model_enrollment_control', 'test-first-model-source-enrollment.py')


class OriginalInvalidationFixtureTests(unittest.TestCase):
    def harness(self, fault=None, timestamp_fault=None):
        tenant, company, service, source, account, owner, first, second = (str(uuid.uuid4()) for _ in range(8))
        posted = []; queries = []; guards = []; enrolled = []
        def post(payload):
            posted.append(copy.deepcopy(payload)); number = len(posted)
            ack = dict(source=dict(tenantId=tenant, companyId=company, sourceBindingId=source),
                messageId=first if number in (1, 3) else second, revision=2 if number == 3 else 1,
                committedSequence=number, committedAtUtc='2026-10-15T20:00:00+00:00', wasAlreadyCommitted=False)
            if number == 3:
                if fault == 'foreign-ack': ack['source']['companyId'] = str(uuid.uuid4())
                if fault == 'wrong-message': ack['messageId'] = second
                if fault == 'bool-revision': ack['revision'] = True
                if fault == 'wrong-sequence': ack['committedSequence'] = 4
                if fault == 'duplicate': ack['wasAlreadyCommitted'] = True
                if fault == 'private-field': ack['private'] = 'must refuse'
                if fault == 'nonutc': ack['committedAtUtc'] = '2026-10-16T03:00:00+07:00'
            if timestamp_fault is not None and number == timestamp_fault[0]:
                ack['committedAtUtc'] = timestamp_fault[1]
            return (503 if number == 3 and fault == 'http' else 200), ack
        def sql(command):
            queries.append(command)
            if command.startswith('SELECT LOWER'): return account
            self.assertIn('UPDATE aioffice.GroupListenerLeases', command)
            for value in (tenant, company, account, owner): self.assertIn(value, command)
            self.assertIn('Epoch=1', command); self.assertIn('ExpiresAtUtc>@now', command)
            return '0' if fault == 'expired' else '1'
        def prepare(**kwargs):
            self.assertIs(kwargs['edit_fixture'], True); kwargs['enroll_source'](source)
            for message in (first, second):
                kwargs['post_event'](dict(event=dict(kind=1, messageId=message, revisionEventId=str(uuid.uuid4()),
                    identity=dict(provider='synthetic', accountId='owned', groupId='owned'), contentSha256='A'*64),
                    text='owned original', listenerOwnerId=owner, listenerEpoch=1))
            return source, [str(uuid.uuid4()), str(uuid.uuid4())]
        base = SimpleNamespace(require_owned=lambda directory, api: guards.append((directory, api)), prepare=prepare)
        result = fixture.prepare(directory=Path('/owned'), api='http://127.0.0.1:8080', tenant=tenant, company=company,
            service=service, sql=sql, identity_index=lambda *a: self.fail('identity should stay in base'),
            enroll_source=enrolled.append, post_event=post, base_fixture=base)
        return SimpleNamespace(source=source, result=result, posted=posted, queries=queries, guards=guards, enrolled=enrolled)

    def test_actual_edit_keeps_identity_and_listener_and_uses_actual_core_ack(self):
        h = self.harness(); source, events, append = h.result
        self.assertEqual(h.source, source); self.assertEqual([source], h.enrolled); self.assertEqual(2, len(events))
        result = append(source); self.assertEqual(200, result[0]); self.assertEqual(3, len(h.posted))
        original, edit = h.posted[0], h.posted[2]
        self.assertEqual(original['event']['messageId'], edit['event']['messageId'])
        self.assertEqual(original['event']['identity'], edit['event']['identity'])
        self.assertEqual(original['listenerOwnerId'], edit['listenerOwnerId'])
        self.assertEqual(3, edit['event']['kind']); self.assertNotEqual(original['event']['revisionEventId'], edit['event']['revisionEventId'])
        self.assertNotEqual(original['text'], edit['text']); self.assertEqual(2, len(h.guards))
        before = len(h.posted)
        with self.assertRaises(AssertionError): append(source)
        self.assertEqual(before, len(h.posted))

    def test_foreign_scope_and_expired_listener_refuse_before_an_edit_call(self):
        h = self.harness()
        with self.assertRaises(AssertionError): h.result[2](str(uuid.uuid4()))
        self.assertEqual(2, len(h.posted)); self.assertEqual(1, len(h.queries))
        h = self.harness('expired')
        with self.assertRaises(AssertionError): h.result[2](h.source)
        self.assertEqual(2, len(h.posted))

    def test_invalid_actual_ack_never_becomes_edit_success(self):
        for fault in ('foreign-ack', 'wrong-message', 'bool-revision', 'wrong-sequence', 'duplicate', 'private-field', 'nonutc', 'http'):
            with self.subTest(fault=fault):
                h = self.harness(fault)
                with self.assertRaises(AssertionError): h.result[2](h.source)

    def test_bad_initial_ack_timestamps_refuse_in_capture_with_fixed_error(self):
        for position in (1, 2):
            for value in ('PRIVATE_TIMESTAMP_42', None, True, 42, [], {}, '', 'X'*41,
                          '2026-10-15T20:00:00', '2026-10-16T03:00:00+07:00'):
                with self.subTest(position=position, value_type=type(value).__name__):
                    with self.assertRaisesRegex(AssertionError, r'^Owned invalidation Core timestamp is not available\.$'):
                        self.harness(timestamp_fault=(position, value))

    def test_bad_edit_ack_timestamps_never_become_success(self):
        for value in ('PRIVATE_TIMESTAMP_42', None, True, 42, [], {}, '', 'X'*41,
                      '2026-10-15T20:00:00', '2026-10-16T03:00:00+07:00'):
            with self.subTest(value_type=type(value).__name__):
                h = self.harness(timestamp_fault=(3, value))
                with self.assertRaisesRegex(AssertionError, r'^Owned invalidation Core timestamp is not available\.$'):
                    h.result[2](h.source)

    def test_private_parser_error_context_is_suppressed_at_every_ack(self):
        canary = ''.join(('PRIVATE_', 'TIMESTAMP_', '42'))
        for position in (1, 2, 3):
            with self.subTest(position=position):
                try:
                    h = self.harness(timestamp_fault=(position, canary))
                    h.result[2](h.source)
                except AssertionError as error:
                    self.assertEqual('Owned invalidation Core timestamp is not available.', str(error))
                    self.assertTrue(error.__suppress_context__)
                    rendered = ''.join(traceback.format_exception(type(error), error, error.__traceback__))
                    self.assertNotIn(canary, rendered)
                    self.assertNotIn('Invalid isoformat string', rendered)
                else: self.fail('Malformed Core time was accepted')

    def test_supported_actual_utc_timestamp_shapes_remain_accepted(self):
        for value in ('2026-10-15T20:00:00Z', '2026-10-15T20:00:00+00:00', '2026-10-15T20:00:00.1234567+00:00'):
            with self.subTest(value=value):
                for position in (1, 2, 3):
                    h = self.harness(timestamp_fault=(position, value))
                    self.assertEqual(200, h.result[2](h.source)[0])

    def test_cold_owned_guard_precedes_callbacks(self):
        def deny(*args): raise RuntimeError('owned refused')
        trap = lambda *args, **kwargs: self.fail('cold guard touched external port')
        with self.assertRaisesRegex(RuntimeError, 'owned refused'):
            fixture.prepare(directory=Path('/foreign'), api='invalid', tenant='invalid', company='invalid', service='invalid',
                sql=trap, identity_index=trap, enroll_source=trap, post_event=trap, base_fixture=SimpleNamespace(require_owned=deny))

    def test_actual_closed_slot_twenty_five_preserves_old_keys_and_refuses_reuse(self):
        h = enrollment.FirstModelSourceEnrollmentTests().harness()
        h.scope['prepare_no_work_source'](); before = copy.deepcopy(h.state)
        source = str(uuid.uuid4()); h.scope['enroll_owned_source'](source, 25)
        for name, value in before.items(): self.assertEqual(value, h.state[name])
        self.assertEqual(source, h.state['AIOffice__GroupIntake__SourceKeys__25__SourceBindingId'])
        self.assertEqual('secretref://env/OWNED_ORIGINAL_INVALIDATION_GROUP_CONTENT_KEY', h.state['AIOffice__GroupIntake__SourceKeys__25__SecretRef'])
        self.assertEqual([5], [item['slot'] for item in h.roles]); state = copy.deepcopy(h.state)
        with self.assertRaises(AssertionError): h.scope['enroll_owned_source'](str(uuid.uuid4()), 25)
        with self.assertRaises(AssertionError): h.scope['enroll_owned_source'](source, 24)
        self.assertEqual(state, h.state)

    def test_reserved_unknown_and_boolean_slots_remain_cold(self):
        h = enrollment.FirstModelSourceEnrollmentTests().harness()
        for slot in (15, 22, 23, 26, True):
            with self.subTest(slot=slot):
                with self.assertRaises(AssertionError): h.scope['enroll_owned_source'](str(uuid.uuid4()), slot)
        self.assertEqual({}, h.state); self.assertEqual([], h.generated)

    def test_actual_ingress_wires_fresh_factory_and_edit_callback(self):
        tree = ast.parse(enrollment.SOURCE.read_text(encoding='utf-8-sig'))
        calls = [node for node in ast.walk(tree) if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
            and isinstance(node.func.value, ast.Name) and node.func.value.id == 'invalidation_proof' and node.func.attr == 'verify']
        self.assertEqual(1, len(calls)); values = {key.arg: key.value for key in calls[0].keywords}
        self.assertEqual('prepare_invalidation_source', values['prepare_source'].id)
        self.assertEqual('edit_invalidation_source', values['edit_source'].id)


if __name__ == '__main__': unittest.main()
