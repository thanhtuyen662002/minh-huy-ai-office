"""Inert orchestration controls only; no native SQL/provider/model qualification."""
import base64
import importlib.util
import io
import json
import os
from pathlib import Path
import re
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module


proof = load('invalidation_driver_controls', 'smoke-group-original-invalidation.py')
reference = load('invalidation_reference_controls', 'smoke-group-reference.py')
automatic = load('invalidation_automatic_controls', 'smoke-group-automatic-notes.py')


class OriginalInvalidationProofTests(unittest.TestCase):
    def run_control(self, fault=None):
        tenant, company, service, source, account, old_source, old_account, installation, first_message, third = (str(uuid.uuid4()) for _ in range(10))
        events = [str(uuid.uuid4()), str(uuid.uuid4())]; phase = 'before'; stage = 0; calls = []; queries = []; callbacks = []
        changed = {
            'GroupIngressInbox': (1, 'B'), 'GroupBatchAllocations': (1, 'B'), 'GroupBatchAllocatedRevisions': (1, 'B'),
            'GroupBatchClaimStates': (2, 'C'), 'GroupBatchClaimReceipts': (2, 'C'), 'GroupAutomaticInputPlans': (2, 'C'),
            'GroupMessageRevisions': (3, 'D'), 'GroupIngressReceipts': (3, 'D'), 'GroupIngressOutbox': (3, 'D'),
            'GroupListenerLeases': (3, 'D'), 'GroupOriginalInvalidations': (4, 'E')}

        def sql(query):
            queries.append(query)
            commands = re.sub(r"N?'(?:''|[^'])*'", "''", query)
            self.assertIsNone(re.search(r'\b(?:UPDATE|INSERT|DELETE|DENY|GRANT|REVOKE|ALTER)\b', commands, re.IGNORECASE))
            if query.startswith('/* owned-original-invalidation-digests */'):
                own = source in query and 'BindingId IN' not in query
                rows = []
                for index, line in enumerate(query.splitlines()[1:]):
                    table = re.search(r'FROM aioffice\.(\w+)', line).group(1)
                    version = 'A'
                    if own:
                        if table == 'GroupSourceStates': version = 'D' if stage >= 3 else 'B' if stage >= 1 else 'A'
                        elif table in changed and stage >= changed[table][0]: version = changed[table][1]
                        if fault == 'unrelated-prepare' and stage >= 2 and table == 'GroupCustomerRequests': version = 'F'
                        if fault == 'original-changed-by-edit' and stage >= 3 and table == 'GroupAutomaticInputPlans': version = 'F'
                        if fault == 'allocation-changed-by-commit' and stage >= 4 and table == 'GroupBatchAllocatedRevisions': version = 'F'
                        if fault == 'replay-graph' and phase == 'invalidation-zero-replay' and table == 'GroupOriginalInvalidations': version = 'F'
                        if fault == 'cleanup-container-graph' and phase == 'container-cleanup' and table == 'GroupModelAttemptResults': version = 'F'
                        if fault == 'cleanup-image-graph' and phase == 'image-cleanup' and table == 'GroupOriginalInvalidations': version = 'F'
                    elif fault == 'retained' and stage >= 2: version = 'F'
                    rows.append(f'{index:02d}|' + version * 64)
                if fault == 'digest-short' and own: rows.pop()
                if fault == 'digest-extra' and own: rows.append(f'{len(rows):02d}|' + 'A' * 64)
                if fault == 'digest-order' and own: rows[0], rows[1] = rows[1], rows[0]
                if fault == 'digest-private' and own: rows[0] = 'PRIVATE_GRAPH_CANARY'
                return '\n'.join(rows)
            if query.startswith('/* owned-original-invalidation-permissions */'):
                return ('F' if fault == 'permissions' and stage >= 3 else 'A') * 64
            if query.startswith('/* owned-original-invalidation-grants */'):
                self.assertIn("EXECUTE AS USER=N'aioffice_runtime';", query)
                self.assertTrue(query.endswith('REVERT;'))
                self.assertIn("ISNULL(HAS_PERMS_BY_NAME(N'aioffice.GroupOriginalInvalidations',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0", query)
                return {'grants': '1|4|13\n1|1|0|0|0|0|0|13|0',
                    'missing-column': '2|4|12\n1|1|0|0|0|0|0|12|0',
                    'unsafe-column': '2|4|13\n1|1|0|0|0|0|0|13|1',
                    'unsafe-object': '2|4|13\n1|1|1|0|0|0|0|13|13',
                    'missing-effective-line': '2|4|13',
                    'extra-effective-line': '2|4|13\n1|1|0|0|0|0|0|13|0\n1',
                    'private-effective-line': '2|4|13\nPRIVATE_PERMISSION_CANARY'}.get(fault, '2|4|13\n1|1|0|0|0|0|0|13|0')
            if query.startswith('/* owned-original-invalidation-counts */'):
                values = [1 if stage >= 2 else 0] + [0] * 17 + [1 if stage >= 4 else 0, 1 if stage >= 2 else 0, 1 if stage >= 2 else 0]
                if fault == 'preseeded': values[0] = 1
                if fault == 'missing-checkpoint' and stage >= 4: values[18] = 0
                if isinstance(fault, tuple) and fault[0] == 'forbidden-row' and stage >= 2: values[proof.ORIGINAL_TABLES.index(fault[1])] = 1
                return '|'.join(map(str, values))
            if query.startswith('/* owned-original-invalidation-allocation */'): return '1|3|2' if fault == 'allocation-count' else '1|2|2'
            if query.startswith('/* owned-original-invalidation-legacy */'): return '0' if fault == 'wrong-original-format' else '1'
            if query.startswith('/* owned-original-invalidation-first */'): return first_message
            if query.startswith('/* owned-original-invalidation-third */'): return events[0] if fault == 'third-event-reused' else third
            if query.startswith('/* owned-original-invalidation-edit */'): return '3|2|3|1|1' if fault == 'incomplete-edit' else '3|3|3|1|1'
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings'): return old_source
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts'): return old_account
            if query.startswith('SELECT LOWER(CONVERT(char(36),ConnectorAccountId))'): return old_account if fault == 'account-reused' else account
            if query.startswith('SET NOCOUNT ON;'):
                return '\n'.join(f'{index:02d}|' + ('F' if fault == 'retained-base' and stage >= 2 else 'A') * 64 for index in range(34))
            self.fail('Unexpected fixed invalidation SQL shape')

        def prepare():
            nonlocal phase
            callbacks.append('prepare'); phase = 'prepared-source'
            if fault == 'prepare-shape': return (source, events)
            selected = old_source if fault == 'source-reused' else source
            selected_events = [events[0], events[0]] if fault == 'events-duplicate' else events
            if fault == 'events-extra': selected_events = [*events, third]
            return selected, selected_events, base64.b64encode(b'k' * 32).decode()

        def edit(source_id):
            nonlocal stage, phase
            callbacks.append('edit'); self.assertEqual(source, source_id); stage = 3; phase = 'core-edit'
            ack = dict(source=dict(tenantId=tenant, companyId=company, sourceBindingId=source), messageId=first_message,
                revision=2, committedSequence=3, committedAtUtc='2026-10-16T00:00:00Z', wasAlreadyCommitted=False)
            if fault == 'edit-shape': return (ack,)
            if fault == 'edit-scope': ack['source']['companyId'] = str(uuid.uuid4())
            if fault == 'edit-message': ack['messageId'] = str(uuid.uuid4())
            if fault == 'edit-revision': ack['revision'] = True
            if fault == 'edit-sequence': ack['committedSequence'] = 4
            if fault == 'edit-replay': ack['wasAlreadyCommitted'] = True
            if fault == 'edit-extra': ack['private'] = 'PRIVATE_ACK_CANARY'
            if fault == 'edit-time': ack['committedAtUtc'] = 'PRIVATE_TIME_CANARY'
            if fault == 'edit-offset': ack['committedAtUtc'] = '2026-10-16T00:00:00+01:00'
            return (500 if fault == 'edit-status' else 200), ack

        def process(command, **kwargs):
            nonlocal phase, stage
            calls.append((command, kwargs))
            if command[:2] == ['docker', 'build']:
                return SimpleNamespace(returncode=1 if fault == 'build' else 0, stdout='', stderr='')
            if command[:2] == ['docker', 'inspect']:
                phase = 'container-cleanup'
                if fault == 'container-cleanup': return SimpleNamespace(returncode=1, stdout='', stderr='unexpected daemon error')
                return SimpleNamespace(returncode=1, stdout='', stderr='Error: No such object: ' + command[-1])
            if command[:3] == ['docker', 'image', 'rm']:
                phase = 'image-cleanup'; return SimpleNamespace(returncode=1 if fault == 'image-cleanup' else 0, stdout='', stderr='')
            self.assertEqual(['docker', 'run'], command[:2]); phase = command[-1]
            self.assertEqual({'tenantId': tenant, 'companyId': company, 'serviceId': service, 'sourceId': source, 'eventId': events[0]}, json.loads(kwargs['input']))
            mode = phase; stage = {'admission-original-prepare': 1, 'invalidation-zero-prepare': 2, 'invalidation-zero-commit': 4, 'invalidation-zero-replay': 4}[mode]
            line = {'admission-original-prepare': proof.ADMISSION_LINE, 'invalidation-zero-prepare': proof.PREPARE_LINE,
                'invalidation-zero-commit': proof.COMMIT_LINE, 'invalidation-zero-replay': proof.REPLAY_LINE}[mode]
            if isinstance(fault, tuple) and fault == ('child-failure', mode): return SimpleNamespace(returncode=1, stdout='', stderr='')
            if fault == 'private-stdout' and mode == 'invalidation-zero-commit': line += '\nPRIVATE_CHILD_CANARY'
            if fault == 'duplicate' and mode == 'invalidation-zero-replay': line += '\n' + line
            if fault == 'wrong-marker' and mode == 'invalidation-zero-prepare': line = proof.COMMIT_LINE
            return SimpleNamespace(returncode=0, stdout=line + '\n', stderr='PRIVATE_STDERR_CANARY' if fault == 'private-stderr' else '')

        args = dict(directory=Path('/owned/aioffice-local'), api='http://127.0.0.1:8080',
            manifest=dict(AIOFFICE_INSTALLATION_ID=installation, AIOFFICE_RUNTIME_PASSWORD='r' * 40),
            tenant=tenant, company=company, service=service, sql=sql, prepare_source=prepare, edit_source=edit,
            reference=reference, automatic=automatic)
        with patch.dict(os.environ, dict(CI='true', GITHUB_ACTIONS='true', RUNNER_TEMP='/owned'), clear=True), patch.object(proof.subprocess, 'run', process), patch('sys.stdout', new_callable=io.StringIO) as output:
            if fault:
                with self.assertRaises(AssertionError): proof.verify(**args)
                self.assertEqual('', output.getvalue())
            else:
                proof.verify(**args); self.assertEqual([proof.SUMMARY], output.getvalue().splitlines())
        return calls, queries, callbacks, args

    def test_success_requires_all_fresh_children_core_edit_and_both_cleanups(self):
        calls, queries, callbacks, args = self.run_control()
        self.assertEqual(['prepare', 'edit'], callbacks)
        self.assertEqual(['admission-original-prepare', 'invalidation-zero-prepare', 'invalidation-zero-commit', 'invalidation-zero-replay'],
            [command[-1] for command, _ in calls if command[:2] == ['docker', 'run']])
        self.assertEqual(2, sum(command[:2] == ['docker', 'inspect'] for command, _ in calls))
        self.assertEqual(1, sum(command[:3] == ['docker', 'image', 'rm'] for command, _ in calls))
        self.assertTrue(any('GroupOriginalInvalidations' in query for query in queries))
        self.assertTrue(any('sys.database_role_members' in query for query in queries))
        for command, kwargs in calls:
            if command[:2] != ['docker', 'run']: continue
            self.assertIn('--read-only', command); self.assertIn('--cap-drop', command); self.assertEqual(170, kwargs['timeout'])
            self.assertNotIn(args['manifest']['AIOFFICE_RUNTIME_PASSWORD'], ' '.join(command) + kwargs['input'])
            self.assertNotIn(kwargs['env']['AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY'], ' '.join(command) + kwargs['input'])

    def test_owned_guard_is_first_and_inert_outside_exact_owned_scope(self):
        for field in ('CI', 'GITHUB_ACTIONS', 'RUNNER_TEMP'):
            env = dict(CI='true', GITHUB_ACTIONS='true', RUNNER_TEMP='/owned'); env.pop(field)
            with self.subTest(field=field), patch.dict(os.environ, env, clear=True), patch.object(proof.subprocess, 'run') as process:
                with self.assertRaises(RuntimeError):
                    proof.verify(directory=Path('/owned/aioffice-local'), api='http://127.0.0.1:8080', manifest=None,
                        tenant=None, company=None, service=None, sql=None, prepare_source=None, edit_source=None, reference=reference, automatic=automatic)
                process.assert_not_called()

    def test_failed_duplicate_private_or_wrong_child_never_qualifies(self):
        for mode in ('admission-original-prepare', 'invalidation-zero-prepare', 'invalidation-zero-commit', 'invalidation-zero-replay'):
            with self.subTest(mode=mode): self.run_control(('child-failure', mode))
        for fault in ('private-stdout', 'private-stderr', 'duplicate', 'wrong-marker'):
            with self.subTest(fault=fault): self.run_control(fault)

    def test_callbacks_and_core_ack_scope_shape_are_checked(self):
        for fault in ('prepare-shape', 'events-duplicate', 'events-extra', 'source-reused', 'account-reused', 'edit-shape',
                'edit-status', 'edit-scope', 'edit-message', 'edit-revision', 'edit-sequence', 'edit-replay', 'edit-extra', 'edit-time', 'edit-offset', 'third-event-reused', 'incomplete-edit'):
            with self.subTest(fault=fault): self.run_control(fault)

    def test_incomplete_duplicate_reordered_or_private_graphs_fail_closed(self):
        for fault in ('digest-short', 'digest-extra', 'digest-order', 'digest-private', 'preseeded', 'missing-checkpoint',
                'allocation-count', 'wrong-original-format', 'unrelated-prepare', 'original-changed-by-edit', 'allocation-changed-by-commit', 'replay-graph'):
            with self.subTest(fault=fault): self.run_control(fault)

    def test_every_original_model_effect_and_terminal_table_is_required_empty(self):
        self.assertEqual(18, len(proof.ORIGINAL_TABLES))
        for table in proof.ORIGINAL_TABLES[1:]:
            with self.subTest(table=table): self.run_control(('forbidden-row', table))

    def test_retained_graphs_permissions_and_exact_grants_cannot_change(self):
        for fault in ('retained', 'retained-base', 'permissions', 'grants'):
            with self.subTest(fault=fault): self.run_control(fault)

    def test_effective_runtime_schema_and_column_rights_are_required(self):
        for fault in ('missing-column', 'unsafe-column', 'unsafe-object', 'missing-effective-line',
                'extra-effective-line', 'private-effective-line'):
            with self.subTest(fault=fault): self.run_control(fault)

    def test_failure_and_both_cleanup_errors_suppress_aggregate(self):
        for fault in ('build', 'container-cleanup', 'image-cleanup', 'cleanup-container-graph', 'cleanup-image-graph'):
            with self.subTest(fault=fault):
                calls, _, _, _ = self.run_control(fault)
                self.assertTrue(any(command[:2] == ['docker', 'inspect'] for command, _ in calls))
                self.assertTrue(any(command[:3] == ['docker', 'image', 'rm'] for command, _ in calls))


if __name__ == '__main__': unittest.main()
