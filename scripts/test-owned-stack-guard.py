"""Verify the adversarial fixture boundary without files, Docker or network."""
import importlib.util
import ast
import copy
import base64
import hashlib
import json
import io
from email.message import Message
import os
import re
from pathlib import Path
import unittest
import tempfile
import uuid
from unittest.mock import patch
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location("owned_stack_smoke", Path(__file__).with_name("smoke-local-stack.py"))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
administrator_spec = importlib.util.spec_from_file_location("administrator_smoke", Path(__file__).with_name("smoke-company-administrators.py"))
administrator_smoke = importlib.util.module_from_spec(administrator_spec)
administrator_spec.loader.exec_module(administrator_smoke)
history_spec = importlib.util.spec_from_file_location("history_smoke", Path(__file__).with_name("smoke-task-history.py"))
history_smoke = importlib.util.module_from_spec(history_spec)
history_spec.loader.exec_module(history_smoke)
submission_spec = importlib.util.spec_from_file_location("submission_smoke", Path(__file__).with_name("smoke-task-submission.py"))
submission_smoke = importlib.util.module_from_spec(submission_spec)
submission_spec.loader.exec_module(submission_smoke)
group_spec = importlib.util.spec_from_file_location("group_ingress_smoke", Path(__file__).with_name("smoke-group-ingress.py"))
group_smoke = importlib.util.module_from_spec(group_spec)
group_spec.loader.exec_module(group_smoke)
listener_spec = importlib.util.spec_from_file_location("listener_smoke", Path(__file__).with_name("smoke-group-listener.py"))
listener_smoke = importlib.util.module_from_spec(listener_spec)
listener_spec.loader.exec_module(listener_smoke)
spool_spec = importlib.util.spec_from_file_location("spool_smoke", Path(__file__).with_name("smoke-group-spool.py"))
spool_smoke = importlib.util.module_from_spec(spool_spec)
spool_spec.loader.exec_module(spool_smoke)
reference_spec = importlib.util.spec_from_file_location("reference_smoke", Path(__file__).with_name("smoke-group-reference.py"))
reference_smoke = importlib.util.module_from_spec(reference_spec)
reference_spec.loader.exec_module(reference_smoke)
recovery_spec = importlib.util.spec_from_file_location("recovery_host_smoke", Path(__file__).with_name("smoke-group-recovery-host.py"))
recovery_smoke = importlib.util.module_from_spec(recovery_spec)
recovery_spec.loader.exec_module(recovery_smoke)
effect_spec = importlib.util.spec_from_file_location("effect_fixture", Path(__file__).with_name("owned-group-effect-fixture.py"))
effect_fixture = importlib.util.module_from_spec(effect_spec)
effect_spec.loader.exec_module(effect_fixture)

automatic_spec = importlib.util.spec_from_file_location("automatic_note_smoke", Path(__file__).with_name("smoke-group-automatic-notes.py"))
automatic_smoke = importlib.util.module_from_spec(automatic_spec)
automatic_spec.loader.exec_module(automatic_smoke)

raw_history_spec = importlib.util.spec_from_file_location("raw_history_smoke", Path(__file__).with_name("smoke-group-raw-history.py"))
raw_history_smoke = importlib.util.module_from_spec(raw_history_spec)
raw_history_spec.loader.exec_module(raw_history_smoke)


class OwnedStackGuardTests(unittest.TestCase):
    def raw_history_runtime_oracle(self, fault=None):
        arguments, original_process, calls, queries = self.automatic_runtime_oracle(fault=fault, proof="no_work")
        original_sql = arguments['sql']; phase = -1
        def sql(query):
            if 'GroupWorkRawDispositions r' in query:
                queries.append(query)
                return {'raw-missing': '499|499|2|497', 'raw-extra': '501|500|2|499', 'raw-head': '500|499|2|498',
                    'raw-scope': '500|0|2|498', 'raw-relation': '500|500|1|499'}.get(fault, '500|500|2|498')
            if 'CONCAT' in query and 'GroupIngressInbox' in query:
                queries.append(query); return '500|1|500' if fault == 'missing-inbox' else '501|1|500'
            if query.startswith('SELECT COUNT(*) FROM aioffice.GroupWorkRawDispositions WHERE '):
                queries.append(query)
                return '1' if fault == 'raw-expiry' and phase in (0, 1) else '500' if phase == 2 else '0'
            if 'HASHBYTES' in query and 'FirstPendingAtUtc,LastPendingAtUtc,ScheduledThroughSequence' in query and 'CAST(500' not in query:
                queries.append(query); return ('B' if fault == 'pending-suffix' and phase >= 0 else 'A') * 64
            return original_sql(query)
        def process(command, **kwargs):
            nonlocal phase
            if command[:2] != ['docker', 'run']: return original_process(command, **kwargs)
            phase = list(raw_history_smoke.RUNTIME_LINES).index(command[-1])
            translated = list(command); translated[-1] = list(automatic_smoke.NO_WORK_RUNTIME_LINES)[phase]
            result = original_process(translated, **kwargs)
            calls[-1] = (command, kwargs)
            result.stdout = result.stdout.replace(automatic_smoke.NO_WORK_RUNTIME_LINES[translated[-1]], raw_history_smoke.RUNTIME_LINES[command[-1]])
            if command[-1] == 'raw-history-commit':
                result.stdout = result.stdout.replace(automatic_smoke.DEPENDENCY_RUNTIME_LINES[translated[-1]],
                    automatic_smoke.DEPENDENCY_RUNTIME_LINES[command[-1]])
            return result
        arguments.pop('proof'); arguments.update(sql=sql, automatic=automatic_smoke)
        return arguments, process, calls, queries

    def test_raw_history_runtime_guard_precedes_configuration_sql_and_process(self):
        def forbidden(*args, **kwargs): self.fail('Unowned raw history touched a resource')
        for invalid in ({'CI': 'false'}, {'GITHUB_ACTIONS': 'false'}, {'RUNNER_TEMP': ''}):
            with patch.dict(os.environ, {**self.environment, **invalid}, clear=True), patch.object(raw_history_smoke.subprocess, 'run', forbidden), self.assertRaises(RuntimeError):
                raw_history_smoke.verify(directory=self.owned, api='http://127.0.0.1:8080', manifest=None,
                    tenant='invalid', company='invalid', service='invalid', sql=forbidden, prepare_source=forbidden,
                    reference=reference_smoke, automatic=None)

    def test_raw_history_runtime_oracle_requires500_raw_cutoff499_heads_retained_pending501_and_full34_snapshots(self):
        arguments, process, calls, queries = self.raw_history_runtime_oracle()
        with patch.dict(os.environ, self.environment, clear=True), patch.object(raw_history_smoke.subprocess, 'run', process), patch('sys.stdout', new_callable=io.StringIO) as output:
            raw_history_smoke.verify(**arguments)
        self.assertEqual(1, len(output.getvalue().splitlines()))
        self.assertTrue(output.getvalue().startswith('PASS actual raw history SQL '))
        runs = [(command, values) for command, values in calls if command[:2] == ['docker', 'run']]
        self.assertEqual(list(raw_history_smoke.RUNTIME_LINES), [command[-1] for command, _ in runs])
        for command, values in runs:
            self.assertEqual(170, values['timeout']); self.assertIn('--read-only', command); self.assertIn('--cap-drop', command)
            self.assertEqual(arguments['manifest']['AIOFFICE_RUNTIME_PASSWORD'] in values['env']['AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION'], True)
            self.assertNotIn(values['env']['AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY'], ' '.join(command) + values['input'])
            self.assertNotIn(arguments['manifest']['AIOFFICE_RUNTIME_PASSWORD'], ' '.join(command) + values['input'])
        joined = next(value for value in queries if 'GroupWorkRawDispositions r' in value)
        for required in ('r.RawRevision', 'r.SelectedMessageRevision=499', 'r.Outcome=6', 'r.SelectedMessageRevision=1',
                'r.Outcome=2', 'b.ObservedCommittedThroughSequence=501', 'b.RawRevisionCount=500', 'r.Relation=CASE', 'd.OperationId=r.OperationId'):
            self.assertIn(required, joined)
        self.assertTrue(any('CAST(500 AS bigint)' in value and 'CommittedSequence=501) AS FirstPendingAtUtc' in value for value in queries))
        snapshots = [value for value in queries if value.startswith('SET NOCOUNT ON;')]
        self.assertEqual(5, len(snapshots)); self.assertTrue(all(value.count('FOR JSON PATH,INCLUDE_NULL_VALUES') == 34 for value in snapshots))
        self.assertEqual(2, sum(command[:2] == ['docker', 'inspect'] for command, _ in calls))
        self.assertEqual(1, sum(command[:3] == ['docker', 'image', 'rm'] for command, _ in calls))

    def test_raw_history_runtime_oracle_refuses_corruption_and_still_cleans_up_without_pass(self):
        for fault in ('raw-missing', 'raw-extra', 'raw-head', 'raw-scope', 'raw-relation', 'raw-expiry', 'missing-inbox',
                'pending-suffix', 'raw-retained-mutation', 'source-mutation', 'partial-effect', 'invented-outbox', 'witness',
                'child-error', 'extra-output', 'stderr'):
            arguments, process, calls, _ = self.raw_history_runtime_oracle(fault)
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True), patch.object(raw_history_smoke.subprocess, 'run', process), patch('sys.stdout', new_callable=io.StringIO) as output, self.assertRaises(AssertionError):
                raw_history_smoke.verify(**arguments)
            self.assertEqual('', output.getvalue())
            self.assertTrue(any(command[:3] == ['docker', 'image', 'rm'] for command, _ in calls))

    def test_automatic_note_runtime_guard_precedes_bad_config_sql_and_process(self):
        def forbidden(*args, **kwargs): self.fail("Unowned automatic proof touched a resource")
        for invalid in ({"CI": "false"}, {"GITHUB_ACTIONS": "false"}, {"RUNNER_TEMP": ""}):
            with patch.dict(os.environ, {**self.environment, **invalid}, clear=True), patch.object(automatic_smoke.subprocess, "run", forbidden):
                with self.assertRaisesRegex(RuntimeError, "owned disposable GitHub CI fixture"):
                    automatic_smoke.verify(directory=self.owned, api="http://127.0.0.1:8080", manifest=None, tenant="invalid",
                        company="invalid", service="invalid", sql=forbidden, prepare_source=forbidden, reference=reference_smoke)

    def test_automatic_retained_snapshot_refuses_noncanonical_empty_or_duplicate_identity_before_sql(self):
        tenant, company, source, account = (str(uuid.uuid4()) for _ in range(4))
        def forbidden(*args): self.fail("Invalid snapshot identity reached SQL")
        for sources, accounts in (([], [account]), ([source, source], [account]), ([source.upper()], [account]),
                ([str(uuid.UUID(int=0))], [account]), ([source], ["';PRIVATE"])):
            with self.assertRaises((AssertionError, ValueError)):
                automatic_smoke.retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=forbidden)

    def test_automatic_retained_snapshot_covers_full_prior_claim_brain_account_registry_and_portal_rows(self):
        tenant, company, first, second, account = (str(uuid.uuid4()) for _ in range(5)); queries = []
        result = automatic_smoke.retained_snapshot(tenant=tenant, company=company, sources=[first, second], accounts=[account],
            sql=lambda query: queries.append(query) or '\n'.join(f"{index:02d}|" + "A" * 64 for index in range(34)))
        self.assertEqual(34, len(result)); self.assertEqual(1, len(queries))
        statements = [value for value in queries[0].split(';') if "HASHBYTES" in value]
        self.assertEqual(34, len(statements))
        for table, _ in automatic_smoke.SOURCE_TABLES:
            query = next(value for value in statements if "FROM aioffice." + table + " WHERE " in value)
            self.assertIn("SELECT *", query); self.assertIn("INCLUDE_NULL_VALUES", query)
            self.assertIn(first, query); self.assertIn(second, query); self.assertIn("ORDER BY BindingId", query)
            order = query.split(" ORDER BY ", 1)[1].split(" FOR JSON", 1)[0].split(',')
            self.assertEqual(len(order), len(set(order)))
        for table in ("GroupBindings", "GroupConnectorAccounts", "GroupListenerLeases", "GroupAccountCoverageGaps",
                "GroupListenerCommandReceipts", "GroupServices", "Users", "Tasks", "TaskDispatches", "TaskCheckpoints"):
            self.assertTrue(any("FROM aioffice." + table + " WHERE " in query for query in statements))
        self.assertFalse(any(re.search(r"\b(INSERT|UPDATE|DELETE)\b", query) for query in queries))

    def test_automatic_retained_snapshot_denies_nonhash_private_result(self):
        tenant, company, source, account = (str(uuid.uuid4()) for _ in range(4))
        for value in ("PRIVATE_SQL_BODY", "a" * 64, "A" * 63, "A" * 65, None):
            with self.assertRaises(AssertionError):
                automatic_smoke.retained_snapshot(tenant=tenant, company=company, sources=[source], accounts=[account], sql=lambda _: value)

    def test_automatic_retained_snapshot_batch_denies_lost_extra_duplicate_reordered_or_private_lines(self):
        tenant, company, source, account = (str(uuid.uuid4()) for _ in range(4))
        lines = [f"{index:02d}|" + "A" * 64 for index in range(34)]
        for value in ('\n'.join(lines[:-1]), '\n'.join(lines + lines[:1]), '\n'.join([lines[0]] * 34),
                '\n'.join(reversed(lines)), '\n'.join(lines[:5] + ["PRIVATE"] + lines[6:]),
                '\n'.join(lines).replace("A", "a"), '\n'.join(lines).replace("00|", "0|", 1)):
            with self.assertRaises(AssertionError):
                automatic_smoke.retained_snapshot(tenant=tenant, company=company, sources=[source], accounts=[account], sql=lambda _: value)

    def automatic_runtime_oracle(self, fault=None, proof="notes"):
        tenant, company, service, old_source, old_account, source, account, installation = (str(uuid.uuid4()) for _ in range(8))
        events = [str(uuid.uuid4()), str(uuid.uuid4())]; phase = -1; calls = []; queries = []
        runtime_lines = {"notes": automatic_smoke.RUNTIME_LINES, "no_work": automatic_smoke.NO_WORK_RUNTIME_LINES,
            "host_only": automatic_smoke.HOST_RUNTIME_LINES, "coverage": automatic_smoke.COVERAGE_RUNTIME_LINES}[proof]
        commit_phase = len(runtime_lines) - 1
        effect_counts = {"notes": (1, 2, 4, 4, 5, 1, 4), "no_work": (1, 2, 0, 0, 0, 0, 0),
            "host_only": (1, 2, 1, 1, 2, 1, 1), "coverage": (0, 0, 0, 0, 0, 0, 0)}[proof]
        def sql(query):
            queries.append(query)
            if query.startswith("SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings"):
                return old_source.upper() if fault == "sql-uuid-uppercase" else old_source
            if query.startswith("SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts"):
                return old_account.upper() if fault == "sql-uuid-uppercase" else old_account
            if query.startswith("SELECT LOWER(CONVERT(char(36),ConnectorAccountId))"):
                return account.upper() if fault == "sql-uuid-uppercase" else account
            if "HASHBYTES" in query:
                if query.startswith("SET NOCOUNT ON;"):
                    value = "B" if fault == "retained-mutation" and phase >= 1 else "A"
                    return '\n'.join(f"{index:02d}|" + ("B" if fault == "raw-retained-mutation" and phase >= 1 and index == 33 else value) * 64 for index in range(34))
                if fault == "retained-mutation" and phase >= 1 and "BindingId IN" in query: return "B" * 64
                if fault == "source-mutation" and phase >= 1 and f"BindingId='{source}'" in query and "GroupMessageRevisions" in query: return "B" * 64
                return "A" * 64
            if "CONCAT" in query:
                if "GroupWorkRawDispositions r" in query:
                    return {"raw-missing": "1|1", "raw-extra": "3|2", "raw-head": "2|1", "raw-scope": "2|0"}.get(fault, "2|2")
                if "GroupAccountCoverageGaps" in query:
                    return "0|1|0" if fault == "gap" else "257|2|0" if proof == "coverage" and phase == commit_phase else "0|0|0"
                if "GroupIngressInbox" in query: return "2|1|2"
                self.fail("Unexpected automatic aggregate oracle")
            if "FROM aioffice.GroupWorkRawDispositions WHERE " in query:
                if fault == "raw-expiry" and phase >= 0 and phase < commit_phase: return "1"
                return "2" if proof != "coverage" and phase == commit_phase else "0"
            if "GroupSourceStates" in query: return "1"
            if "GroupBatchClaimReceipts" in query: return str(max(0, phase))
            if "GroupBatchClaimStates" in query: return "0" if fault == "witness" or phase <= 0 else "1"
            if "Origin=" in query: return "1" if proof == "host_only" else "2"
            if "Outcome=1" in query: return "1"
            if "Outcome=2" in query: return "1" if "GroupWorkCommitReceipts" in query else "2"
            if "Outcome=3" in query: return "1"
            if "Outcome=7" in query or "Kind=2" in query: return "2"
            if proof == "coverage" and "GroupCoverageGaps" in query:
                return "0" if fault == "coverage-incomplete" else "1" if "ReconnectedAtUtc IS NOT NULL" in query else "257"
            if proof == "coverage" and "GroupAccountCoverageGaps" in query: return "2"
            for table, count in zip(automatic_smoke.EFFECT_TABLES, effect_counts):
                if "FROM aioffice." + table + " WHERE " in query:
                    if fault == "partial-effect" and phase == commit_phase and table == ("GroupRequestEvidence" if proof == "notes" else "GroupWorkSourceDispositions"):
                        return "4" if proof == "notes" else "1"
                    if fault == "invented-outbox" and phase == commit_phase and table == "GroupNotesCommittedOutbox": return "1"
                    if fault == "invented-ai" and phase == commit_phase and table == "GroupRequestRevisions": return "2"
                    if fault == "expiry-effect" and phase == 1 and table == "GroupCustomerRequests": return "1"
                    return str(count if phase == commit_phase else 0)
            if "GroupIngressInbox" in query or "GroupBatchAllocations" in query or "GroupBatchAllocatedRevisions" in query: return "0"
            self.fail("Unexpected automatic SQL oracle")
        def process(command, **kwargs):
            nonlocal phase
            calls.append((command, kwargs))
            if command[:2] == ["docker", "build"]: return SimpleNamespace(returncode=0, stdout="", stderr="")
            if command[:3] == ["docker", "image", "rm"]: return SimpleNamespace(returncode=0, stdout="", stderr="")
            if command[:2] == ["docker", "inspect"]:
                return SimpleNamespace(returncode=1, stdout="", stderr="Error: No such object: " + command[-1])
            mode = command[-1]; phase = list(runtime_lines).index(mode)
            expected = runtime_lines[mode]
            if mode in automatic_smoke.DEPENDENCY_RUNTIME_LINES: expected += '\n' + automatic_smoke.DEPENDENCY_RUNTIME_LINES[mode]
            return SimpleNamespace(returncode=1 if fault == "child-error" else 0,
                stdout=expected + ("\nPRIVATE\n" if fault == "extra-output" else "\n"), stderr="PRIVATE" if fault == "stderr" else "")
        arguments = dict(directory=self.owned, api="http://127.0.0.1:8080", manifest={"AIOFFICE_INSTALLATION_ID": installation,
            "AIOFFICE_RUNTIME_PASSWORD": "p" * 32}, tenant=tenant, company=company, service=service, sql=sql,
            prepare_source=lambda: (source, events, base64.b64encode(b"k" * 32).decode("ascii")), reference=reference_smoke, proof=proof)
        return arguments, process, calls, queries

    def test_automatic_raw_retained_snapshot_is_appended_without_replacing_prior33_queries(self):
        tenant, company, source, account = (str(uuid.uuid4()) for _ in range(4)); queries = []
        automatic_smoke.retained_snapshot(tenant=tenant, company=company, sources=[source], accounts=[account],
            sql=lambda query: queries.append(query) or '\n'.join(f"{index:02d}|" + "A" * 64 for index in range(34)))
        statements = [value for value in queries[0].split(';') if "HASHBYTES" in value]
        self.assertEqual(34, len(statements))
        self.assertIn("N'33|'", statements[-1]); self.assertIn("SELECT * FROM aioffice.GroupWorkRawDispositions", statements[-1])
        self.assertIn("ORDER BY BindingId,BatchId,CommittedSequence", statements[-1])
        self.assertIn("INCLUDE_NULL_VALUES", statements[-1]); self.assertIn(source, statements[-1])
        self.assertFalse(any("GroupWorkRawDispositions" in value for value in statements[:-1]))

    def test_automatic_raw_oracle_refuses_missing_extra_wrong_head_or_foreign_scope_before_pass(self):
        for proof in ("notes", "no_work", "host_only"):
            for fault in ("raw-missing", "raw-extra", "raw-head", "raw-scope"):
                with self.subTest(proof=proof, fault=fault):
                    arguments, process, calls, _ = self.automatic_runtime_oracle(fault, proof)
                    with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", side_effect=process), patch('builtins.print') as output:
                        with self.assertRaises(AssertionError): automatic_smoke.verify(**arguments)
                        output.assert_not_called()
                    self.assertTrue(any(command[:3] == ['docker', 'image', 'rm'] for command, _ in calls))

    def test_automatic_raw_oracle_refuses_partial_expiry_or_changed_retained_raw_scope_before_pass(self):
        for proof in ("notes", "no_work", "host_only", "coverage"):
            for fault in ("raw-expiry", "raw-retained-mutation"):
                with self.subTest(proof=proof, fault=fault):
                    arguments, process, calls, _ = self.automatic_runtime_oracle(fault, proof)
                    with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", side_effect=process), patch('builtins.print') as output:
                        with self.assertRaises(AssertionError): automatic_smoke.verify(**arguments)
                        output.assert_not_called()
                    self.assertTrue(any(command[:3] == ['docker', 'image', 'rm'] for command, _ in calls))

    def test_automatic_sql_uuid_projection_keeps_all_profiles_canonical_without_weakening_identity_guard(self):
        value = "abcdef12-3456-4789-9abc-def012345678"
        self.assertEqual(value, automatic_smoke.canonical(value))
        for malformed in (value.upper(), "{" + value + "}", value.replace("-", ""), str(uuid.UUID(int=0))):
            with self.subTest(value=malformed), self.assertRaises(AssertionError): automatic_smoke.canonical(malformed)
        for proof in ("notes", "no_work", "host_only"):
            arguments, process, _, queries = self.automatic_runtime_oracle(proof=proof)
            with self.subTest(proof=proof), patch.dict(os.environ, self.environment, clear=True), \
                    patch.object(automatic_smoke.subprocess, "run", process), patch("sys.stdout", new_callable=io.StringIO) as output:
                automatic_smoke.verify(**arguments)
            self.assertEqual(1 if proof == "coverage" else 2, len(output.getvalue().splitlines()))
            projections = [query for query in queries if query.startswith("SELECT LOWER(CONVERT(char(36),")]
            self.assertEqual(3, len(projections))
            self.assertTrue(any("Id)) FROM aioffice.GroupBindings" in query for query in projections))
            self.assertTrue(any("Id)) FROM aioffice.GroupConnectorAccounts" in query for query in projections))
            self.assertTrue(any("ConnectorAccountId)) FROM aioffice.GroupBindings" in query for query in projections))

    def test_automatic_noncanonical_sql_guid_result_refuses_before_fixture_or_docker(self):
        for proof in ("notes", "no_work", "host_only"):
            arguments, process, calls, _ = self.automatic_runtime_oracle("sql-uuid-uppercase", proof=proof)
            arguments["prepare_source"] = lambda: self.fail("Noncanonical SQL identity reached fixture enrollment")
            with self.subTest(proof=proof), patch.dict(os.environ, self.environment, clear=True), \
                    patch.object(automatic_smoke.subprocess, "run", process), patch("sys.stdout", new_callable=io.StringIO) as output, self.assertRaises(AssertionError):
                automatic_smoke.verify(**arguments)
            self.assertEqual([], calls); self.assertEqual("", output.getvalue())

    def test_coverage_profile_requires_two_closed_modes_exact_metadata_and_zero_effects(self):
        arguments, process, calls, queries = self.automatic_runtime_oracle(proof="coverage")
        with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process), \
                patch("sys.stdout", new_callable=io.StringIO) as output:
            automatic_smoke.verify(**arguments)
        self.assertEqual(1, len(output.getvalue().splitlines())); self.assertIn("PASS actual coverage SQL", output.getvalue())
        children = [(command, options) for command, options in calls if command[:2] == ["docker", "run"]]
        self.assertEqual(list(automatic_smoke.COVERAGE_RUNTIME_LINES), [command[-1] for command, _ in children])
        for clause in ("AfterCommittedSequence=2", "ReconnectedAtUtc IS NOT NULL", "ListenerEpoch=1 AND Reason IN"):
            self.assertTrue(any(clause in query for query in queries))
        for command, options in children:
            self.assertEqual(170, options["timeout"]); self.assertIn("--read-only", command)
            self.assertNotIn("p" * 32, " ".join(command)); self.assertNotIn("p" * 32, options["input"])
        self.assertEqual(["docker", "image", "rm"], calls[-1][0][:3])

    def test_coverage_profile_refuses_partial_metadata_effects_mutation_or_child_error_without_pass(self):
        for fault in ("coverage-incomplete", "invented-outbox", "retained-mutation", "source-mutation", "gap",
                "child-error", "extra-output", "stderr", "sql-uuid-uppercase"):
            arguments, process, calls, _ = self.automatic_runtime_oracle(fault, proof="coverage")
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True), \
                    patch.object(automatic_smoke.subprocess, "run", process), patch("sys.stdout", new_callable=io.StringIO) as output, self.assertRaises(AssertionError):
                automatic_smoke.verify(**arguments)
            self.assertEqual("", output.getvalue())
            if calls: self.assertEqual(["docker", "image", "rm"], calls[-1][0][:3])

    def test_coverage_profile_owned_guard_precedes_configuration_and_callbacks(self):
        def forbidden(*args, **kwargs): self.fail("Unowned coverage reached a resource")
        with patch.dict(os.environ, {}, clear=True), patch.object(automatic_smoke.subprocess, "run", forbidden), self.assertRaises(RuntimeError):
            automatic_smoke.verify(directory=self.owned, api="http://127.0.0.1:8080", manifest=None,
                tenant="invalid", company="invalid", service="invalid", sql=forbidden, prepare_source=forbidden,
                reference=reference_smoke, proof="coverage")

    def test_automatic_runtime_oracle_requires_all_four_modes_complete_graph_and_private_key_environment(self):
        arguments, process, calls, queries = self.automatic_runtime_oracle()
        with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process), patch("sys.stdout", new_callable=io.StringIO) as output:
            automatic_smoke.verify(**arguments)
        self.assertEqual(2, len(output.getvalue().splitlines()))
        children = [(command, kwargs) for command, kwargs in calls if command[:2] == ["docker", "run"]]
        self.assertEqual(list(automatic_smoke.RUNTIME_LINES), [command[-1] for command, _ in children])
        self.assertEqual(["docker", "image", "rm"], calls[-1][0][:3])
        for command, kwargs in children:
            self.assertIn("--read-only", command); self.assertIn("no-new-privileges:true", command)
            self.assertTrue(kwargs["capture_output"]); self.assertEqual(170, kwargs["timeout"])
            self.assertNotIn("p" * 32, " ".join(command)); self.assertNotIn("p" * 32, kwargs["input"])
            self.assertIn("AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY", kwargs["env"])
        self.assertTrue(any("CAST(NULL AS datetimeoffset(7))" in query for query in queries))

    def test_automatic_dependency_markers_are_exact_and_required_for_each_current_profile(self):
        for proof in ('notes', 'no_work', 'host_only'):
            for fault in ('missing', 'changed', 'duplicate', 'reversed', 'extra'):
                arguments, original_process, _, _ = self.automatic_runtime_oracle(proof=proof)
                def process(command, **kwargs):
                    result = original_process(command, **kwargs)
                    if command[:2] == ['docker', 'run'] and command[-1] in automatic_smoke.DEPENDENCY_RUNTIME_LINES:
                        lines = result.stdout.splitlines()
                        if fault == 'missing': lines = lines[:1]
                        elif fault == 'changed': lines[1] = 'PRIVATE_WRONG_DEPENDENCY_MARKER'
                        elif fault == 'duplicate': lines.append(lines[1])
                        elif fault == 'reversed': lines.reverse()
                        else: lines.append('PRIVATE_UNRELATED')
                        result.stdout = '\n'.join(lines) + '\n'
                    return result
                with self.subTest(proof=proof, fault=fault), patch.dict(os.environ, self.environment, clear=True), \
                        patch.object(automatic_smoke.subprocess, 'run', process), patch('sys.stdout', new_callable=io.StringIO) as output, \
                        self.assertRaises(AssertionError) as raised:
                    automatic_smoke.verify(**arguments)
                self.assertNotIn('PRIVATE', str(raised.exception)); self.assertEqual('', output.getvalue())

    def test_raw_history_dependency_marker_is_required_and_cannot_replace_original_history_evidence(self):
        for fault in ('missing-original', 'missing-dependency', 'duplicate', 'reversed', 'extra'):
            arguments, original_process, _, _ = self.raw_history_runtime_oracle()
            def process(command, **kwargs):
                result = original_process(command, **kwargs)
                if command[:2] == ['docker', 'run'] and command[-1] == 'raw-history-commit':
                    lines = result.stdout.splitlines()
                    if fault == 'missing-original': lines = lines[1:]
                    elif fault == 'missing-dependency': lines = lines[:1]
                    elif fault == 'duplicate': lines.append(lines[1])
                    elif fault == 'reversed': lines.reverse()
                    else: lines.append('PRIVATE_UNRELATED')
                    result.stdout = '\n'.join(lines) + '\n'
                return result
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True), \
                    patch.object(raw_history_smoke.subprocess, 'run', process), patch('sys.stdout', new_callable=io.StringIO) as output, \
                    self.assertRaises(AssertionError) as raised:
                raw_history_smoke.verify(**arguments)
            self.assertNotIn('PRIVATE', str(raised.exception)); self.assertEqual('', output.getvalue())

    def test_automatic_runtime_oracle_denies_wrong_child_outputs_and_restores_owned_image(self):
        for fault in ("child-error", "extra-output", "stderr", "retained-mutation", "source-mutation", "gap", "partial-effect", "expiry-effect"):
            arguments, process, calls, _ = self.automatic_runtime_oracle(fault)
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process), \
                    patch("sys.stdout", new_callable=io.StringIO) as output, self.assertRaises(AssertionError):
                automatic_smoke.verify(**arguments)
            self.assertEqual("", output.getvalue())
            if calls: self.assertEqual(["docker", "image", "rm"], calls[-1][0][:3])

    def test_automatic_no_work_profile_refuses_unowned_or_unknown_profile_before_configuration_callbacks(self):
        def forbidden(*args, **kwargs): self.fail("Invalid no-work profile touched a resource")
        arguments = dict(directory=self.owned, api="http://127.0.0.1:8080", manifest=None, tenant="invalid",
            company="invalid", service="invalid", sql=forbidden, prepare_source=forbidden, reference=reference_smoke)
        with patch.dict(os.environ, {}, clear=True), patch.object(automatic_smoke.subprocess, "run", forbidden), self.assertRaises(RuntimeError):
            automatic_smoke.verify(**arguments, proof="no_work")
        for proof in (None, True, 1, "unknown", "no_work;"):
            with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", forbidden), self.assertRaises(AssertionError):
                automatic_smoke.verify(**arguments, proof=proof)

    def test_automatic_no_work_runtime_oracle_requires_three_modes_full_three_effects_no_notes_and_private_key_environment(self):
        arguments, process, calls, queries = self.automatic_runtime_oracle(proof="no_work")
        with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process), patch("sys.stdout", new_callable=io.StringIO) as output:
            automatic_smoke.verify(**arguments)
        self.assertEqual(2, len(output.getvalue().splitlines())); self.assertIn("PASS actual automatic no-work SQL", output.getvalue())
        self.assertIn("PASS actual automatic no-work raw SQL two exact selected-head rows", output.getvalue())
        children = [(command, kwargs) for command, kwargs in calls if command[:2] == ["docker", "run"]]
        self.assertEqual(list(automatic_smoke.NO_WORK_RUNTIME_LINES), [command[-1] for command, _ in children])
        self.assertTrue(any("Outcome=2 AND NoteCount=0 AND SelectedMessageCount=2" in query for query in queries))
        self.assertTrue(any("Outcome=2 AND MessageRevision=1" in query for query in queries))
        self.assertTrue(any("ExpiryObservedAtUtc =ExpiresAtUtc" in query for query in queries))
        self.assertTrue(any("ExpiryObservedAtUtc IS NULL" in query for query in queries))
        for command, kwargs in children:
            self.assertIn("--read-only", command); self.assertIn("no-new-privileges:true", command); self.assertEqual(170, kwargs["timeout"])
            self.assertNotIn("p" * 32, " ".join(command)); self.assertNotIn("p" * 32, kwargs["input"])
            self.assertIn("AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY", kwargs["env"])
        self.assertEqual(["docker", "image", "rm"], calls[-1][0][:3])

    def test_automatic_no_work_oracle_denies_partial_effects_invented_outbox_missing_witness_mutation_or_child_failure(self):
        for fault in ("child-error", "extra-output", "stderr", "retained-mutation", "source-mutation", "gap", "partial-effect", "expiry-effect", "invented-outbox", "witness"):
            arguments, process, calls, _ = self.automatic_runtime_oracle(fault, proof="no_work")
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process), \
                    patch("sys.stdout", new_callable=io.StringIO) as output, self.assertRaises(AssertionError):
                automatic_smoke.verify(**arguments)
            self.assertEqual("", output.getvalue())
            if calls: self.assertEqual(["docker", "image", "rm"], calls[-1][0][:3])

    def test_automatic_host_runtime_oracle_requires_four_modes_one_host_note_two_attention_dispositions_nine_effects_no_model(self):
        arguments, process, calls, queries = self.automatic_runtime_oracle(proof="host_only")
        with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process), patch("sys.stdout", new_callable=io.StringIO) as output:
            automatic_smoke.verify(**arguments)
        self.assertEqual(2, len(output.getvalue().splitlines())); self.assertIn("PASS actual automatic host SQL", output.getvalue())
        self.assertIn("PASS actual automatic host raw SQL two exact selected-head rows", output.getvalue())
        children = [(command, kwargs) for command, kwargs in calls if command[:2] == ["docker", "run"]]
        self.assertEqual(list(automatic_smoke.HOST_RUNTIME_LINES), [command[-1] for command, _ in children])
        for clause in ("Outcome=3 AND NoteCount=1 AND SelectedMessageCount=2", "Outcome=7 AND MessageRevision=1",
                "Origin=3 AND VerificationLevel=3", "Kind=2"):
            self.assertTrue(any(clause in query for query in queries))
        for command, kwargs in children:
            self.assertIn("--read-only", command); self.assertEqual(170, kwargs["timeout"])
            self.assertNotIn("p" * 32, " ".join(command)); self.assertNotIn("p" * 32, kwargs["input"])
        self.assertEqual(["docker", "image", "rm"], calls[-1][0][:3])

    def test_automatic_host_oracle_denies_partial_effects_invented_ai_missing_witness_mutation_or_child_failure(self):
        for fault in ("child-error", "extra-output", "stderr", "retained-mutation", "source-mutation", "gap", "partial-effect", "expiry-effect", "invented-ai", "witness"):
            arguments, process, calls, _ = self.automatic_runtime_oracle(fault, proof="host_only")
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process), \
                    patch("sys.stdout", new_callable=io.StringIO) as output, self.assertRaises(AssertionError):
                automatic_smoke.verify(**arguments)
            self.assertEqual("", output.getvalue())
            if calls: self.assertEqual(["docker", "image", "rm"], calls[-1][0][:3])

    def test_automatic_container_cleanup_guards_before_inspect_and_denies_foreign_or_unverified_identity(self):
        suffix = uuid.uuid4().hex; name = "aioffice-automatic-note-proof-" + suffix
        arguments = dict(directory=self.owned, api="http://127.0.0.1:8080", name=name, suffix=suffix, reference=reference_smoke)
        def forbidden(*args, **kwargs): self.fail("Unowned cleanup touched Docker")
        with patch.dict(os.environ, {}, clear=True), patch.object(automatic_smoke.subprocess, "run", forbidden), self.assertRaises(RuntimeError):
            automatic_smoke.close_owned_container(**arguments)
        for result in (SimpleNamespace(returncode=0, stdout="a" * 64 + "|foreign", stderr=""),
                SimpleNamespace(returncode=0, stdout="not-an-id|" + suffix, stderr=""),
                SimpleNamespace(returncode=1, stdout="", stderr="Daemon unavailable PRIVATE")):
            calls = []
            with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run",
                    lambda command, **kwargs: calls.append(command) or result), self.assertRaises(AssertionError):
                automatic_smoke.close_owned_container(**arguments)
            self.assertEqual(2, len(calls)); self.assertTrue(all(command[:2] == ["docker", "inspect"] for command in calls))

    def test_automatic_container_cleanup_removes_only_inspected_ID_and_rechecks_partial_startup(self):
        suffix = uuid.uuid4().hex; name = "aioffice-automatic-note-proof-" + suffix; identity = "a" * 64
        arguments = dict(directory=self.owned, api="http://127.0.0.1:8080", name=name, suffix=suffix, reference=reference_smoke)
        for delayed in (False, True):
            calls = []; inspections = 0
            def process(command, **kwargs):
                nonlocal inspections
                calls.append((command, kwargs))
                if command[:2] == ["docker", "inspect"]:
                    inspections += 1
                    if (delayed and inspections == 1) or (not delayed and inspections == 2):
                        return SimpleNamespace(returncode=1, stdout="", stderr="Error: No such object: " + name)
                    return SimpleNamespace(returncode=0, stdout=identity + "|" + suffix, stderr="")
                self.assertEqual(["docker", "rm", "--force", identity], command)
                return SimpleNamespace(returncode=0, stdout=identity, stderr="")
            with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process):
                automatic_smoke.close_owned_container(**arguments)
            self.assertEqual(2, inspections); self.assertEqual(1, sum(command[1] == "rm" for command, _ in calls))
            self.assertTrue(all(kwargs["timeout"] <= 15 and kwargs["capture_output"] for _, kwargs in calls))

    def test_automatic_owned_absence_accepts_only_empty_or_docker_line_ending_stdout(self):
        suffix = uuid.uuid4().hex; name = "aioffice-automatic-note-proof-" + suffix
        for stdout in ("", "\n", "\r\n"):
            for error in ("Error: No such object: ", "Error: No such container: ", "Error response from daemon: No such container: "):
                calls = []
                def process(command, **kwargs):
                    calls.append(command)
                    return SimpleNamespace(returncode=1, stdout=stdout, stderr=error + name)
                with self.subTest(stdout=repr(stdout), error=error), patch.dict(os.environ, self.environment, clear=True), \
                        patch.object(automatic_smoke.subprocess, "run", process):
                    automatic_smoke.close_owned_container(directory=self.owned, api="http://127.0.0.1:8080",
                        name=name, suffix=suffix, reference=reference_smoke)
                self.assertEqual(2, len(calls)); self.assertTrue(all(command[:2] == ["docker", "inspect"] for command in calls))

    def test_automatic_owned_absence_rejects_other_stdout_errors_names_and_exit_codes(self):
        suffix = uuid.uuid4().hex; name = "aioffice-automatic-note-proof-" + suffix
        results = [SimpleNamespace(returncode=1, stdout=value, stderr="Error: No such object: " + name)
            for value in ("[]\n", "{}\n", " \n", "\t", "\u00a0", "\n\n", "foreign")]
        results += [SimpleNamespace(returncode=code, stdout="\n", stderr=error) for code, error in (
            (2, "Error: No such object: " + name), (1, "Error: No such object: foreign"), (1, "Daemon unavailable"))]
        for result in results:
            calls = []
            with self.subTest(stdout=repr(result.stdout), code=result.returncode), patch.dict(os.environ, self.environment, clear=True), \
                    patch.object(automatic_smoke.subprocess, "run", lambda command, **kwargs: calls.append(command) or result), self.assertRaises(AssertionError):
                automatic_smoke.close_owned_container(directory=self.owned, api="http://127.0.0.1:8080",
                    name=name, suffix=suffix, reference=reference_smoke)
            self.assertEqual(2, len(calls)); self.assertTrue(all(command[:2] == ["docker", "inspect"] for command in calls))

    def test_automatic_owned_removal_race_accepts_exact_id_absence_with_docker_line_ending(self):
        suffix = uuid.uuid4().hex; name = "aioffice-automatic-note-proof-" + suffix; identity = "a" * 64
        for stdout in ("", "\n", "\r\n"):
            for error in ("Error: No such container: ", "Error response from daemon: No such container: "):
                calls = []
                def process(command, **kwargs):
                    calls.append(command)
                    if len(calls) == 1: return SimpleNamespace(returncode=0, stdout=identity + "|" + suffix, stderr="")
                    if command[1] == "rm":
                        self.assertEqual(["docker", "rm", "--force", identity], command)
                        return SimpleNamespace(returncode=1, stdout=stdout, stderr=error + identity)
                    return SimpleNamespace(returncode=1, stdout="\n", stderr="Error: No such object: " + name)
                with self.subTest(stdout=repr(stdout), error=error), patch.dict(os.environ, self.environment, clear=True), \
                        patch.object(automatic_smoke.subprocess, "run", process):
                    automatic_smoke.close_owned_container(directory=self.owned, api="http://127.0.0.1:8080",
                        name=name, suffix=suffix, reference=reference_smoke)
                self.assertEqual(["inspect", "rm", "inspect"], [command[1] for command in calls])

    def test_automatic_timeout_cleanup_attempts_container_then_image_and_preserves_first_failure(self):
        arguments, original_process, calls, _ = self.automatic_runtime_oracle()
        first = automatic_smoke.subprocess.TimeoutExpired(["owned-child"], 170)
        identity = "a" * 64; active = False
        def process(command, **kwargs):
            nonlocal active
            if command[:2] == ["docker", "run"]:
                calls.append((command, kwargs)); active = True; raise first
            if command[:2] == ["docker", "inspect"] and active:
                calls.append((command, kwargs))
                return SimpleNamespace(returncode=0, stdout=identity + "|" + command[-1].removeprefix("aioffice-automatic-note-proof-"), stderr="")
            if command[:3] == ["docker", "rm", "--force"]:
                calls.append((command, kwargs)); self.assertEqual(identity, command[-1]); active = False
                return SimpleNamespace(returncode=0, stdout=identity, stderr="")
            if command[:3] == ["docker", "image", "rm"]:
                calls.append((command, kwargs)); raise RuntimeError("owned cleanup secondary failure")
            return original_process(command, **kwargs)
        with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process), \
                self.assertRaises(automatic_smoke.subprocess.TimeoutExpired) as raised:
            automatic_smoke.verify(**arguments)
        self.assertIs(first, raised.exception); self.assertFalse(active)
        self.assertEqual(["run", "inspect", "rm", "inspect", "image"], [command[1] for command, _ in calls[1:]])

    def test_automatic_cleanup_retries_owned_removal_after_failure_without_replacing_first_error(self):
        suffix = uuid.uuid4().hex; name = "aioffice-automatic-note-proof-" + suffix; identity = "a" * 64
        first = RuntimeError("owned first removal error"); calls = []
        def process(command, **kwargs):
            calls.append(command)
            if command[:2] == ["docker", "inspect"]:
                return SimpleNamespace(returncode=0, stdout=identity + "|" + suffix, stderr="")
            if len(calls) == 2: raise first
            self.assertEqual(["docker", "rm", "--force", identity], command)
            return SimpleNamespace(returncode=0, stdout=identity, stderr="")
        with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", process), self.assertRaises(RuntimeError) as raised:
            automatic_smoke.close_owned_container(directory=self.owned, api="http://127.0.0.1:8080", name=name, suffix=suffix, reference=reference_smoke)
        self.assertIs(first, raised.exception); self.assertEqual(["inspect", "rm", "inspect", "rm"], [command[1] for command in calls])

    def test_automatic_runtime_reports_success_only_after_verified_container_and_image_cleanup(self):
        arguments, process, calls, _ = self.automatic_runtime_oracle()
        first = RuntimeError("owned image removal failed")
        def fail_cleanup(command, **kwargs):
            if command[:3] == ["docker", "image", "rm"]:
                calls.append((command, kwargs)); raise first
            return process(command, **kwargs)
        with patch.dict(os.environ, self.environment, clear=True), patch.object(automatic_smoke.subprocess, "run", fail_cleanup), \
                patch("sys.stdout", new_callable=io.StringIO) as output, self.assertRaises(RuntimeError) as raised:
            automatic_smoke.verify(**arguments)
        self.assertIs(first, raised.exception); self.assertEqual("", output.getvalue())
        self.assertEqual(2, sum(command[:2] == ["docker", "inspect"] for command, _ in calls))

    def clean_effect_callbacks(self, fault=None):
        tenant, company, service = (str(uuid.uuid4()) for _ in range(3))
        calls, enrolled, messages = [], [], []
        events = [str(uuid.uuid4()), str(uuid.uuid4())]
        def sql(statement):
            if "INSERT aioffice.GroupConnectorAccounts" in statement:
                calls.append(("registry", statement))
                if fault == "registry": raise RuntimeError("owned-registry-failure")
                return ""
            if "INSERT aioffice.GroupListenerLeases" in statement:
                calls.append(("lease", statement)); return ""
            if "GroupAccountCoverageGaps" in statement:
                calls.append(("gaps", statement)); return "0|1|0" if fault == "gap" else "0|0|0"
            if "GroupIngressOutbox" in statement:
                calls.append(("events", statement))
                return "\n".join([events[0], events[0]] if fault == "duplicate-events" else events + events[:1] if fault == "extra-events" else events)
            self.fail("Unexpected inert clean effect SQL")
        def enroll(source):
            calls.append(("enroll", source)); enrolled.append(source)
            if fault == "enroll": raise RuntimeError("owned-enrollment-failure")
        def post(payload):
            calls.append(("post", payload)); messages.append(payload)
            sequence = len(messages)
            value = {"source": {"tenantId": tenant, "companyId": company, "sourceBindingId": enrolled[0]},
                "messageId": str(uuid.uuid4()), "revision": 1, "committedSequence": sequence,
                "committedAtUtc": "2026-10-11T01:00:00+00:00", "wasAlreadyCommitted": False}
            if fault == "foreign-source": value["source"]["sourceBindingId"] = str(uuid.uuid4())
            if fault == "unknown-field": value["private"] = "PRIVATE"
            if fault == "bool-revision": value["revision"] = True
            if fault == "bool-sequence": value["committedSequence"] = True
            if fault == "replay": value["wasAlreadyCommitted"] = True
            return (403 if fault == "http" else 200), value
        arguments = dict(directory=self.owned, api="http://127.0.0.1:8080", tenant=tenant, company=company,
            service=service, sql=sql, identity_index=lambda *values: hashlib.sha256("\n".join(values).encode()).hexdigest().upper(),
            enroll_source=enroll, post_event=post)
        return arguments, calls, events

    def test_clean_effect_fixture_guards_before_every_callback(self):
        for invalid in ({"CI": "false"}, {"GITHUB_ACTIONS": "false"}, {"RUNNER_TEMP": ""}):
            arguments, calls, _ = self.clean_effect_callbacks()
            with patch.dict(os.environ, {**self.environment, **invalid}, clear=True), self.assertRaises(RuntimeError):
                effect_fixture.prepare(**arguments)
            self.assertEqual([], calls)
        for field, value in (("api", "https://example.com"), ("directory", self.owned / "other")):
            arguments, calls, _ = self.clean_effect_callbacks(); arguments[field] = value
            with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(RuntimeError): effect_fixture.prepare(**arguments)
            self.assertEqual([], calls)

    def raw_history_callbacks(self, fault=None):
        arguments, calls, events = self.clean_effect_callbacks(); original_sql = arguments['sql']; original_post = arguments['post_event']
        account = str(uuid.uuid4()); first_ack = None; count = 0
        def sql(query):
            if 'LOWER(CONVERT(char(36),ConnectorAccountId))' in query:
                calls.append(('account', query)); return account
            if 'UPDATE aioffice.GroupListenerLeases' in query:
                calls.append(('renew', query)); return '0' if fault == 'lease' else '1'
            if 'GroupMessageRevisions' in query and 'GroupSourceStates' in query:
                calls.append(('raw-counts', query)); return '500|501|501|2|501|0' if fault == 'counts' else '501|501|501|2|501|0'
            return original_sql(query)
        def post(payload):
            nonlocal first_ack, count
            status, value = original_post(payload); count += 1
            if count == 1: first_ack = dict(value)
            if isinstance(fault, tuple) and fault[0] == 'time' and count == fault[1]: value['committedAtUtc'] = fault[2]
            if count > 2:
                value.update(messageId=first_ack['messageId'], revision=count-1)
                if count == 3:
                    if fault == 'message': value['messageId'] = str(uuid.uuid4())
                    if fault == 'revision': value['revision'] = True
                    if fault == 'sequence': value['committedSequence'] = True
                    if fault == 'scope': value['source']['companyId'] = str(uuid.uuid4())
                    if fault == 'extra-field': value['private'] = 'PRIVATE'
                    if fault == 'replay': value['wasAlreadyCommitted'] = True
                    if fault == 'http': status = 503
            return status, value
        arguments.update(sql=sql, post_event=post)
        return arguments, calls, events

    def test_raw_history_fixture_refuses_non_utc_or_malformed_time_in_all501_ack_positions(self):
        for sequence in (1, 2, 3, 500, 501):
            for timestamp in ('2026-10-11', '2026-10-11T00:00:00', '2026-10-11T00:00:00+07:00', 'invalid', 1):
                arguments, calls, _ = self.raw_history_callbacks(('time', sequence, timestamp))
                with self.subTest(sequence=sequence, timestamp=timestamp), patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError):
                    effect_fixture.prepare_raw_history(**arguments)
                self.assertEqual(sequence, len([value for name, value in calls if name == 'post']))
                self.assertFalse(any(name == 'raw-counts' for name, _ in calls))

    def test_raw_history_fixture_keeps_two_originals_then499_edits_and_bounded_own_live_lease(self):
        arguments, calls, events = self.raw_history_callbacks()
        with patch.dict(os.environ, self.environment, clear=True): source, actual = effect_fixture.prepare_raw_history(**arguments)
        self.assertEqual(events, actual); self.assertEqual(source, calls[1][1])
        registry = next(value for name, value in calls if name == 'registry')
        qualification = json.loads(re.search(r"N'(\{\"environment\":1,\"observations\":.*?\})'", registry).group(1))
        self.assertEqual(1, qualification['environment']); self.assertEqual(1, len(qualification['observations']))
        observation = qualification['observations'][0]
        self.assertEqual({'capability', 'support', 'evidenceId', 'observedAtUtc'}, set(observation))
        self.assertEqual(8, observation['capability']); self.assertEqual(1, observation['support'])
        self.assertNotEqual(0, uuid.UUID(observation['evidenceId']).int)
        from datetime import datetime, timezone
        observed = datetime.fromisoformat(observation['observedAtUtc'])
        self.assertEqual(timezone.utc.utcoffset(observed), observed.utcoffset())
        payloads = [value for name, value in calls if name == 'post']
        self.assertEqual(501, len(payloads)); self.assertEqual([1, 1], [value['event']['kind'] for value in payloads[:2]])
        self.assertTrue(all(value['event']['kind'] == 3 and value['event']['messageId'] == payloads[0]['event']['messageId'] for value in payloads[2:]))
        self.assertNotEqual(payloads[0]['event']['messageId'], payloads[1]['event']['messageId'])
        self.assertEqual(501, len({value['event']['revisionEventId'] for value in payloads}))
        self.assertTrue(all(value['text'] == '' and value['event']['contentSha256'] == hashlib.sha256(b'').hexdigest().upper() for value in payloads))
        renewals = [value for name, value in calls if name == 'renew']; self.assertEqual(20, len(renewals))
        for query in renewals:
            self.assertIn('ExpiresAtUtc>@now', query); self.assertIn('HeartbeatAtUtc<=@now', query)
            self.assertIn("OwnerId='" + payloads[0]['listenerOwnerId'] + "' AND Epoch=1", query)
            self.assertNotIn('GroupListenerCommandReceipts', query); self.assertNotIn('GroupCoverageGaps', query)

    def test_edit_observation_is_history_only_and_flag_cannot_bypass_owned_fixture_guard(self):
        for prepare in (effect_fixture.prepare, effect_fixture.prepare_automatic, effect_fixture.prepare_no_work, effect_fixture.prepare_host_only):
            arguments, calls, _ = self.clean_effect_callbacks()
            with patch.dict(os.environ, self.environment, clear=True): prepare(**arguments)
            registry = next(value for name, value in calls if name == 'registry')
            self.assertIn("N'{\"environment\":1,\"observations\":[]}'", registry)
        for flag in (None, 1, 'true'):
            arguments, calls, _ = self.clean_effect_callbacks()
            with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError):
                effect_fixture.prepare(**arguments, edit_fixture=flag)
            self.assertEqual([], calls)
        arguments, calls, _ = self.clean_effect_callbacks()
        with patch.dict(os.environ, {}, clear=True), self.assertRaises(RuntimeError):
            effect_fixture.prepare(**arguments, edit_fixture=True)
        self.assertEqual([], calls)

    def test_raw_history_fixture_refuses_expired_foreign_lease_and_every_malformed_edit_ack(self):
        for fault in ('lease', 'message', 'revision', 'sequence', 'scope', 'extra-field', 'replay', 'http', 'counts'):
            arguments, calls, _ = self.raw_history_callbacks(fault)
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError):
                effect_fixture.prepare_raw_history(**arguments)
            self.assertLessEqual(len([value for name, value in calls if name == 'post']), 501)
            if fault != 'counts': self.assertFalse(any(name == 'raw-counts' for name, _ in calls))

    def test_raw_history_fixture_deadline_refuses_before_or_after_an_edit_ack(self):
        for times, posts in (([0, 300], 2), ([0, 1, 300], 3), ([0, 301], 2), ([0, 1, 301], 3)):
            arguments, calls, _ = self.raw_history_callbacks()
            with self.subTest(times=times), patch.dict(os.environ, self.environment, clear=True), \
                    patch.object(effect_fixture.time, 'monotonic', side_effect=times), self.assertRaises(AssertionError) as raised:
                effect_fixture.prepare_raw_history(**arguments)
            self.assertEqual(f'Owned raw history generation deadline exceeded (lastSequence={posts}, elapsedSeconds=300)', str(raised.exception))
            self.assertEqual(posts, len([value for name, value in calls if name == 'post']))
            self.assertFalse(any(name == 'raw-counts' for name, _ in calls))

    def test_raw_history_fixture_keeps_all501_ack_checks_through_a_bounded_slow_core_window(self):
        for elapsed in (121, 299.999):
            arguments, calls, events = self.raw_history_callbacks()
            with self.subTest(elapsed=elapsed), patch.dict(os.environ, self.environment, clear=True), \
                    patch.object(effect_fixture.time, 'monotonic', side_effect=[0] + [elapsed] * 998):
                _, actual = effect_fixture.prepare_raw_history(**arguments)
            self.assertEqual(events, actual); self.assertEqual(501, len([value for name, value in calls if name == 'post']))
            self.assertEqual(20, len([value for name, value in calls if name == 'renew']))
            self.assertEqual(1, len([value for name, value in calls if name == 'raw-counts']))

    def test_raw_history_fixture_owned_guard_precedes_callbacks_or_time(self):
        arguments, calls, _ = self.raw_history_callbacks()
        with patch.dict(os.environ, {}, clear=True), patch.object(effect_fixture.time, 'monotonic', side_effect=AssertionError('resource')), self.assertRaises(RuntimeError):
            effect_fixture.prepare_raw_history(**arguments)
        self.assertEqual([], calls)
        for field, value in (("api", "https://example.com"), ("directory", self.owned / "other")):
            arguments, calls, _ = self.raw_history_callbacks(); arguments[field] = value
            with patch.dict(os.environ, self.environment, clear=True), patch.object(effect_fixture.time, 'monotonic', side_effect=AssertionError('resource')), self.assertRaises(RuntimeError):
                effect_fixture.prepare_raw_history(**arguments)
            self.assertEqual([], calls)

    def test_automatic_effect_fixture_guards_before_callbacks_and_invalid_handler(self):
        arguments, calls, _ = self.clean_effect_callbacks()
        with patch.dict(os.environ, {}, clear=True), self.assertRaises(RuntimeError): effect_fixture.prepare_automatic(**arguments)
        self.assertEqual([], calls)
        arguments["post_event"] = None
        with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError): effect_fixture.prepare_automatic(**arguments)
        self.assertEqual([], calls)

    def test_automatic_effect_fixture_posts_two_closed_media_inputs_with_exact_UTF8_hashes(self):
        arguments, calls, events = self.clean_effect_callbacks()
        with patch.dict(os.environ, self.environment, clear=True): source, actual_events = effect_fixture.prepare_automatic(**arguments)
        self.assertEqual(events, actual_events)
        self.assertEqual(["registry", "enroll", "lease", "post", "post", "gaps", "events"], [value[0] for value in calls])
        self.assertEqual(source, calls[1][1]); self.assertIn("DATEADD(second,30,@now)", calls[2][1])
        first, second = calls[3][1], calls[4][1]
        self.assertEqual("Tra cứu tồn kho 😀\uFEFF ", first["text"])
        self.assertEqual("Password=OWNED_AUTOMATIC_PRIVATE_SENTINEL", second["text"])
        for payload in (first, second):
            self.assertEqual(2, payload["event"]["kind"])
            self.assertEqual(hashlib.sha256(payload["text"].encode("utf-8")).hexdigest().upper(), payload["event"]["contentSha256"])
            self.assertTrue(payload["isGroup"]); self.assertFalse(payload["isSelf"]); self.assertFalse(payload["isKnownReportEcho"])
        self.assertEqual(first["event"]["identity"], second["event"]["identity"])

    def test_automatic_effect_fixture_retains_foreign_replay_gap_and_cardinality_denials(self):
        for fault in ("http", "foreign-source", "unknown-field", "bool-revision", "bool-sequence", "replay",
                "gap", "duplicate-events", "extra-events"):
            arguments, calls, _ = self.clean_effect_callbacks(fault)
            with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError): effect_fixture.prepare_automatic(**arguments)
            self.assertEqual(2 if fault in ("gap", "duplicate-events", "extra-events") else 1, sum(x[0] == "post" for x in calls))

    def test_no_work_effect_fixture_guard_precedes_callbacks_and_invalid_handler(self):
        arguments, calls, _ = self.clean_effect_callbacks()
        with patch.dict(os.environ, {}, clear=True), self.assertRaises(RuntimeError): effect_fixture.prepare_no_work(**arguments)
        self.assertEqual([], calls); arguments["post_event"] = None
        with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError): effect_fixture.prepare_no_work(**arguments)
        self.assertEqual([], calls)

    def test_no_work_effect_fixture_posts_only_two_empty_plain_sources_with_exact_hash_after_readiness_lease(self):
        arguments, calls, events = self.clean_effect_callbacks()
        with patch.dict(os.environ, self.environment, clear=True): source, actual_events = effect_fixture.prepare_no_work(**arguments)
        self.assertEqual(events, actual_events)
        self.assertEqual(["registry", "enroll", "lease", "post", "post", "gaps", "events"], [value[0] for value in calls])
        self.assertEqual(source, calls[1][1]); self.assertIn("DATEADD(second,30,@now)", calls[2][1])
        for payload in (calls[3][1], calls[4][1]):
            self.assertEqual("", payload["text"]); self.assertEqual(1, payload["event"]["kind"])
            self.assertEqual(hashlib.sha256(b"").hexdigest().upper(), payload["event"]["contentSha256"])
        self.assertNotEqual(calls[3][1]["event"]["messageId"], calls[4][1]["event"]["messageId"])

    def test_no_work_effect_fixture_preserves_strict_receipts_gaps_and_cardinality(self):
        for fault in ("http", "foreign-source", "unknown-field", "bool-revision", "bool-sequence", "replay", "gap", "duplicate-events", "extra-events"):
            arguments, calls, _ = self.clean_effect_callbacks(fault)
            with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError): effect_fixture.prepare_no_work(**arguments)
            self.assertEqual(2 if fault in ("gap", "duplicate-events", "extra-events") else 1, sum(x[0] == "post" for x in calls))

    def test_host_only_effect_fixture_guard_precedes_callbacks_and_invalid_handler(self):
        arguments, calls, _ = self.clean_effect_callbacks()
        with patch.dict(os.environ, {}, clear=True), self.assertRaises(RuntimeError): effect_fixture.prepare_host_only(**arguments)
        self.assertEqual([], calls); arguments["post_event"] = None
        with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError): effect_fixture.prepare_host_only(**arguments)
        self.assertEqual([], calls)

    def test_host_only_effect_fixture_posts_only_two_empty_media_with_exact_empty_hash_and_complete_receipts(self):
        arguments, calls, events = self.clean_effect_callbacks()
        with patch.dict(os.environ, self.environment, clear=True): source, actual_events = effect_fixture.prepare_host_only(**arguments)
        self.assertEqual(events, actual_events); self.assertEqual(source, calls[1][1])
        self.assertEqual(["registry", "enroll", "lease", "post", "post", "gaps", "events"], [value[0] for value in calls])
        for payload in (calls[3][1], calls[4][1]):
            self.assertEqual("", payload["text"]); self.assertEqual(2, payload["event"]["kind"])
            self.assertEqual(hashlib.sha256(b"").hexdigest().upper(), payload["event"]["contentSha256"])

    def test_host_only_effect_fixture_preserves_strict_receipts_gaps_and_cardinality(self):
        for fault in ("http", "foreign-source", "unknown-field", "bool-revision", "bool-sequence", "replay", "gap", "duplicate-events", "extra-events"):
            arguments, calls, _ = self.clean_effect_callbacks(fault)
            with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError): effect_fixture.prepare_host_only(**arguments)
            self.assertEqual(2 if fault in ("gap", "duplicate-events", "extra-events") else 1, sum(x[0] == "post" for x in calls))

    def test_clean_effect_fixture_validates_identity_and_callbacks_before_sql(self):
        for field, value in (("tenant", "invalid"), ("service", str(uuid.UUID(int=0))), ("post_event", None),
                ("enroll_source", None), ("identity_index", lambda *args: "'; PRIVATE")):
            arguments, calls, _ = self.clean_effect_callbacks(); arguments[field] = value
            with patch.dict(os.environ, self.environment, clear=True), self.assertRaises((AssertionError, ValueError)):
                effect_fixture.prepare(**arguments)
            self.assertEqual([], calls)

    def test_clean_effect_fixture_orders_seed_after_readiness_and_exact_two_core_events(self):
        arguments, calls, events = self.clean_effect_callbacks()
        with patch.dict(os.environ, self.environment, clear=True): source, actual_events = effect_fixture.prepare(**arguments)
        self.assertEqual(events, actual_events)
        self.assertEqual(["registry", "enroll", "lease", "post", "post", "gaps", "events"], [value[0] for value in calls])
        self.assertEqual(source, calls[1][1])
        self.assertIn("BEGIN TRANSACTION", calls[0][1]); self.assertIn("COMMIT", calls[0][1])
        self.assertIn("SYSUTCDATETIME()", calls[2][1]); self.assertIn("DATEADD(second,30,@now)", calls[2][1])
        self.assertEqual(calls[3][1]["event"]["identity"], calls[4][1]["event"]["identity"])
        self.assertNotEqual(calls[3][1]["event"]["messageId"], calls[4][1]["event"]["messageId"])
        self.assertEqual(1, calls[3][1]["listenerEpoch"])
        self.assertEqual(calls[3][1]["listenerOwnerId"], calls[4][1]["listenerOwnerId"])
        for _, value in calls:
            if isinstance(value, str): self.assertNotRegex(value, r"\b(?:DELETE|UPDATE)\b")

    def test_clean_effect_fixture_stops_before_lease_after_setup_or_enrollment_failure(self):
        for fault, stages in (("registry", ["registry"]), ("enroll", ["registry", "enroll"])):
            arguments, calls, _ = self.clean_effect_callbacks(fault)
            with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(RuntimeError): effect_fixture.prepare(**arguments)
            self.assertEqual(stages, [value[0] for value in calls])

    def test_clean_effect_fixture_rejects_nonoriginal_or_foreign_core_ack_without_retry(self):
        for fault in ("http", "foreign-source", "unknown-field", "bool-revision", "bool-sequence", "replay"):
            arguments, calls, _ = self.clean_effect_callbacks(fault)
            with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError): effect_fixture.prepare(**arguments)
            self.assertEqual(["registry", "enroll", "lease", "post"], [value[0] for value in calls])

    def test_clean_effect_fixture_rejects_persisted_gap_or_wrong_event_cardinality(self):
        for fault in ("gap", "duplicate-events", "extra-events"):
            arguments, calls, _ = self.clean_effect_callbacks(fault)
            with patch.dict(os.environ, self.environment, clear=True), self.assertRaises(AssertionError): effect_fixture.prepare(**arguments)
            self.assertEqual(2, sum(value[0] == "post" for value in calls))

    def test_clean_effect_runtime_guards_before_configuration_or_callbacks(self):
        def forbidden(*args, **kwargs): self.fail("Unowned fixed-effect proof touched a resource")
        with patch.dict(os.environ, {}, clear=True), self.assertRaisesRegex(RuntimeError, "owned disposable GitHub CI fixture"):
            reference_smoke.verify_fixed_effects(directory=self.owned, api="http://127.0.0.1:8080", tenant="invalid",
                company="invalid", service="invalid", source="invalid", events=None, source_key=None, sql=forbidden,
                command=None, child_environment=None, assert_retained=forbidden)

    def test_reference_success_requires_original_exact_output_and_exit(self):
        expected = ["PASS owned test original"]
        reference_smoke.require_reference_result(SimpleNamespace(stdout=expected[0], returncode=0), "no-work-expiry", expected)
        for output, code in ((expected[0], 1), (expected[0] + "\nextra", 0), ("", 0)):
            with self.subTest(code=code, output=output), self.assertRaises(AssertionError):
                reference_smoke.require_reference_result(SimpleNamespace(stdout=output, returncode=code), "no-work-expiry", expected)

    def test_reference_failure_exports_only_known_no_work_phase(self):
        for mode in ("no-work-expiry", "no-work-commit", "no-work-mars"):
            for phase in ("setup", "claim", "source", "preparation", "brain", "dependencies", "commit", "savepoint",
                    "flush", "rollback", "expirychecks", "clockrollback", "replay", "duplicatechecks", "unknown"):
                output = "FAIL owned NoWork runtime phase-" + phase + "\nFAIL owned reference runtime " + mode
                with self.subTest(mode=mode, phase=phase), self.assertRaisesRegex(AssertionError, "exit=1; phase=" + phase + "$"):
                    reference_smoke.require_reference_result(SimpleNamespace(stdout=output, returncode=1), mode, ["PASS original"])

    def test_reference_failure_never_exports_arbitrary_output_or_stderr(self):
        private = "PRIVATE_CONNECTION_TOKEN_SOURCE"
        for output, code, mode in ((private, 1, "no-work-expiry"),
                ("FAIL owned NoWork runtime phase-" + private + "\nFAIL owned reference runtime no-work-expiry", 1, "no-work-expiry"),
                ("FAIL owned NoWork runtime phase-flush\nFAIL owned reference runtime no-work-expiry\n" + private, 1, "no-work-expiry"),
                ("FAIL owned NoWork runtime phase-flush\nFAIL owned reference runtime no-work-expiry", 2, "no-work-expiry"),
                ("FAIL owned NoWork runtime phase-flush\nFAIL owned reference runtime note-expiry", 1, "note-expiry"),
                (private, private, "no-work-expiry")):
            with self.subTest(code=code, mode=mode), self.assertRaises(AssertionError) as refusal:
                reference_smoke.require_reference_result(SimpleNamespace(stdout=output, stderr=private, returncode=code), mode, ["PASS original"])
            self.assertNotIn(private, str(refusal.exception))
            self.assertNotIn("phase=", str(refusal.exception))

    def test_statistics_child_has_independent_name_with_original_owned_arguments(self):
        suffix = "a" * 32
        command = ["docker", "run", "--init", "--rm", "--name", "aioffice-reference-proof-" + suffix,
            "--label", "aioffice.owned-proof=" + suffix, "--network", "owned-network", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m", "-i",
            "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF",
            "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION", "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_BROKER_PASSWORD", "owned-image"]
        original = command.copy()
        stats = reference_smoke.reference_statistics_command(command)
        self.assertEqual(original, command)
        self.assertEqual("aioffice-reference-stats-" + suffix, stats[command.index("--name") + 1])
        self.assertEqual([command.index("--name") + 1], [index for index in range(len(command)) if command[index] != stats[index]])
        self.assertEqual(stats, reference_smoke.reference_statistics_command(command))

    def test_statistics_child_refuses_foreign_malformed_or_duplicate_ownership(self):
        suffix = "a" * 32
        valid = ["docker", "run", "--name", "aioffice-reference-proof-" + suffix, "--label", "aioffice.owned-proof=" + suffix, "owned-image"]
        for command in ((), [], ["--name"], valid + ["--name", "second"], valid + ["--label", "foreign"],
            [*valid[:3], "foreign", *valid[4:]], [*valid[:5], "aioffice.owned-proof=" + "b" * 32, valid[-1]]):
            with self.subTest(command=command), self.assertRaises(AssertionError):
                reference_smoke.reference_statistics_command(command)

    def test_held_native_delivery_requires_exact_sample_before_the_owned_kill(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        attempt = next(node for node in verify.body if isinstance(node, ast.Try) and any(isinstance(child, ast.FunctionDef)
            and child.name == "unsafe_column" for child in node.body))
        observation = next(index for index, node in enumerate(attempt.body) if isinstance(node, ast.Expr)
            and isinstance(node.value, ast.Call) and isinstance(node.value.func, ast.Name)
            and node.value.func.id == "wait_reference_statistics" and "before owned held delivery kill" in ast.unparse(node))
        kill = attempt.body[observation + 1]
        self.assertIsInstance(kill, ast.Assert)
        self.assertIn("kill_owned()", ast.unparse(kill))
        block = compile(ast.Module(body=attempt.body[observation:observation + 2], type_ignores=[]), "<actual-held-before-kill>", "exec")
        for snapshots, succeeds in (([(0, 0, 1), (0, 1, 1)], True), ([(0, 1, 0), (0, 1, 1)], True),
                ([(0, 0, 1)] * 2, False), ([(1, 1, 1)] * 2, False), ([(0, 2, 1)] * 2, False),
                ([(0, 1, 0)] * 2, False), ([(0, 1, 2)] * 2, False), ([(0, 0, 1), (1, 1, 1)], False)):
            with self.subTest(snapshots=snapshots):
                frames = iter(snapshots); calls = []
                def statistics():
                    ack, deliver, consumers = next(frames)
                    return dict(ack=ack, deliver=deliver, consumers=consumers)
                def bounded_wait(predicate, seconds=30):
                    self.assertEqual(30, seconds)
                    for _ in snapshots:
                        if predicate(): return
                    raise AssertionError("Owned observation deadline exceeded")
                def kill_owned(): calls.append("kill"); return True
                namespace = dict(wait_reference_statistics=reference_smoke.wait_reference_statistics,
                    broker_stats=statistics, wait=bounded_wait, kill_owned=kill_owned)
                if succeeds:
                    exec(block, namespace); self.assertEqual(["kill"], calls)
                else:
                    with self.assertRaisesRegex(AssertionError, "before owned held delivery kill"):
                        exec(block, namespace)
                    self.assertEqual([], calls)

    def brain_fixture(self):
        value = {field: str(uuid.uuid4()) for field in ("requestId", "glossaryId", "publisherId", "batchId", "messageId")}
        value.update({"claimEpoch": 4, "sourceVersion": 1, "deletionGeneration": 0, "messageRevision": 1,
            "credentialEpoch": 1, "grantVersion": 1, "accountVersion": 1, "createdAtUtc": "2026-10-10T16:00:00.0000001+00:00"})
        for kind in ("request", "glossary"):
            cipher = bytes([1]) + bytes([0x32 if kind == "request" else 0x33]) * 40
            value[kind + "Envelope"] = cipher.hex().upper()
            value[kind + "Hash"] = hashlib.sha256(cipher).hexdigest().upper()
        value["sourceSetHash"] = hashlib.sha256(f"{value['messageId']}/1".encode("ascii")).hexdigest().upper()
        return value

    def test_brain_fixture_accepts_only_closed_bounded_cipher_metadata(self):
        value = self.brain_fixture()
        self.assertEqual(value, reference_smoke.validated_brain_fixture(json.dumps(value)))
        self.assertEqual(18, len(value))

    def test_brain_fixture_rejects_sql_shaped_or_unbounded_or_inconsistent_metadata(self):
        for field, invalid in (("requestId", "'; DROP TABLE aioffice.Tasks;--"), ("glossaryId", str(uuid.UUID(int=0))),
                ("publisherId", None), ("claimEpoch", 3), ("claimEpoch", True), ("sourceVersion", 0),
                ("deletionGeneration", -1), ("grantVersion", 9223372036854775808), ("accountVersion", 1.0),
                ("messageRevision", 0), ("createdAtUtc", "2026-10-10T16:00:00.0000001+00:00'; PRIVATE"),
                ("createdAtUtc", "2026-10-10T16:00:00.0000001+01:00"), ("createdAtUtc", "2026-99-10T16:00:00.0000001+00:00"),
                ("requestEnvelope", "01" * 1025), ("requestEnvelope", "01" * 29), ("requestEnvelope", "02" * 30),
                ("requestHash", "F" * 64), ("glossaryHash", "F" * 64), ("sourceSetHash", "F" * 64),
                ("glossaryEnvelope", None), ("PRIVATE_KEY", "PRIVATE")):
            with self.subTest(field=field, invalid=invalid):
                value = self.brain_fixture(); value[field] = invalid
                with self.assertRaises((AssertionError, ValueError)): reference_smoke.validated_brain_fixture(json.dumps(value))

    def test_brain_fixture_rejects_duplicate_fields_before_sql_and_total_output_overflow(self):
        raw = json.dumps(self.brain_fixture(), separators=(",", ":"))
        duplicate = raw.replace('"claimEpoch":4', '"claimEpoch":4,"claimEpoch":4')
        for invalid in (duplicate, raw + " " * 8192, "[]", "null"):
            with self.assertRaises(AssertionError): reference_smoke.validated_brain_fixture(invalid)

    def work_schema_fixture(self, fail=False):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name in
            ("work_permission_catalog", "unsafe_work_column")]
        loops = [node for node in ast.walk(tree) if isinstance(node, ast.For) and isinstance(node.target, ast.Tuple)
            and [getattr(x, "id", None) for x in node.target.elts] == ["table", "column"]
            and any(isinstance(x, ast.Constant) and x.value == "GroupCustomerRequests" for x in ast.walk(node.iter))]
        self.assertEqual(2, len(functions)); self.assertEqual(1, len(loops))
        allowed = {"GroupCustomerRequests": "RequestCode", "GroupEditorGrants": "IsEnabled"}
        permissions = {name: False for name in allowed}; calls = []
        def sql(command):
            calls.append(command)
            table = next((name for name in allowed if name in command), None)
            self.assertIsNotNone(table)
            if "sys.database_permissions" in command:
                self.assertIn("grantee_principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_runtime')", command)
                self.assertIn("ORDER BY minor_id,type", command)
                return ("B" if permissions[table] else "A") * 64
            if command.startswith("GRANT UPDATE"): permissions[table] = True; return ""
            if command.startswith("DENY UPDATE"): permissions[table] = False; return ""
            self.assertTrue(command.startswith("EXECUTE AS LOGIN=N'aioffice_runtime';"))
            self.assertTrue(command.endswith(" REVERT;")); self.assertIn("N'COLUMN'", command)
            self.assertIn("N'" + allowed[table] + "'", command)
            return "1" if permissions[table] else "0"
        runs = []
        def run(mode):
            runs.append(mode)
            if mode == "work-unsafe":
                self.assertEqual(1, sum(permissions.values()))
                if fail: raise RuntimeError("Owned fixture failure")
            else:
                self.assertEqual("work-schema", mode); self.assertFalse(any(permissions.values()))
        namespace = {"sql": sql, "run": run, "re": re, "temporary_sql": reference_smoke.temporary_sql}
        exec(compile(ast.Module(body=functions, type_ignores=[]), '<actual-work-schema-functions>', 'exec'), namespace)
        return namespace, permissions, calls, runs, compile(ast.Module(body=loops, type_ignores=[]), '<actual-work-schema-loop>', 'exec')

    def test_work_schema_effective_column_escalations_require_exact_catalog_restore_and_positive(self):
        namespace, permissions, calls, runs, loop = self.work_schema_fixture()
        exec(loop, namespace)
        self.assertEqual(["work-unsafe", "work-schema", "work-unsafe", "work-schema"], runs)
        self.assertFalse(any(permissions.values()))
        self.assertEqual(2, sum(command.startswith("GRANT UPDATE") for command in calls))
        self.assertEqual(2, sum(command.startswith("DENY UPDATE") for command in calls))
        self.assertEqual(4, sum("sys.database_permissions" in command for command in calls))

    def test_work_schema_failed_unsafe_control_restores_original_permission_and_cannot_run_positive(self):
        namespace, permissions, calls, runs, loop = self.work_schema_fixture(fail=True)
        with self.assertRaisesRegex(RuntimeError, "^Owned fixture failure$"): exec(loop, namespace)
        self.assertFalse(any(permissions.values())); self.assertEqual(["work-unsafe"], runs)
        self.assertEqual(1, sum(command.startswith("DENY UPDATE") for command in calls))

    def test_work_schema_catalog_refuses_foreign_table_before_sql(self):
        namespace, _, calls, _, _ = self.work_schema_fixture()
        for table in ("Tasks", "GroupGlossaryRevisions", "GroupCustomerRequests; PRIVATE", None):
            with self.assertRaises(AssertionError): namespace["work_permission_catalog"](table)
        self.assertEqual([], calls)

    def test_source_checkpoint_type_binding_waits_for_container_without_image_fallback(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name in
            ("source_reader_container", "source_reader_awaiting")]
        self.assertEqual(2, len(functions))
        compiled = compile(ast.Module(body=functions, type_ignores=[]), '<actual-source-type-binding>', 'exec')
        identity, label = "a" * 64, "b" * 32
        calls = []
        created = False
        def run(arguments, **kwargs):
            calls.append(arguments)
            if arguments[:2] == ["docker", "inspect"]:
                typed = arguments[2:4] == ["--type", "container"]
                if not created:
                    return SimpleNamespace(returncode=1 if typed else 0,
                        stdout="" if typed else "sha256:" + identity + "|<no value>")
                return SimpleNamespace(returncode=0, stdout=identity + "|" + label)
            self.assertEqual(["docker", "exec", identity, "test", "-f", "/tmp/aioffice-source-proof-awaiting"], arguments)
            return SimpleNamespace(returncode=0)
        namespace = {"subprocess": SimpleNamespace(run=run), "re": re, "name": "aioffice-reference-proof-" + label, "suffix": label}
        exec(compiled, namespace)
        self.assertFalse(namespace["source_reader_awaiting"]())
        self.assertEqual(1, len(calls))
        created = True
        self.assertTrue(namespace["source_reader_awaiting"]())
        self.assertEqual(3, len(calls))
        self.assertEqual(["--type", "container"], calls[0][2:4])
        self.assertEqual(["--type", "container"], calls[1][2:4])

    def test_owned_kill_type_binding_cannot_select_same_named_image(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "kill_owned"]
        self.assertEqual(1, len(functions))
        compiled = compile(ast.Module(body=functions, type_ignores=[]), '<actual-kill-type-binding>', 'exec')
        identity, label = "a" * 64, "b" * 32
        calls = []
        created = False
        def run(arguments, **kwargs):
            calls.append(arguments)
            if arguments[:2] == ["docker", "inspect"]:
                typed = arguments[2:4] == ["--type", "container"]
                if not created:
                    return SimpleNamespace(returncode=1 if typed else 0,
                        stdout="" if typed else "sha256:" + identity + "|<no value>")
                return SimpleNamespace(returncode=0, stdout=identity + "|" + label)
            self.assertEqual(["docker", "kill", identity], arguments)
            return SimpleNamespace(returncode=0)
        namespace = {"subprocess": SimpleNamespace(run=run), "re": re, "name": "aioffice-reference-proof-" + label, "suffix": label}
        exec(compiled, namespace)
        self.assertFalse(namespace["kill_owned"]())
        self.assertEqual(1, len(calls))
        created = True
        self.assertTrue(namespace["kill_owned"]())
        self.assertEqual(3, len(calls))
        self.assertEqual(["--type", "container"], calls[0][2:4])
        self.assertEqual(["--type", "container"], calls[1][2:4])

    def test_reference_source_key_is_canonical_and_refusal_never_echoes_input(self):
        reference_smoke.require_source_key(base64.b64encode(bytes([0x32]) * 32).decode("ascii"))
        for value in (None, "", "owned-private-key-detail", "A" * 44, "A" * 43 + "!", base64.b64encode(bytes(31)).decode("ascii")):
            with self.subTest(kind=type(value).__name__):
                with self.assertRaisesRegex(RuntimeError, r"^Owned reference source key is unavailable\.$") as refusal:
                    reference_smoke.require_source_key(value)
                self.assertIsNone(refusal.exception.__cause__)

    def test_source_key_gate_checks_owned_container_before_observation_or_release(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name in
            ("source_reader_container", "source_reader_awaiting", "release_source_reader")]
        self.assertEqual(3, len(functions))
        compiled = compile(ast.Module(body=functions, type_ignores=[]), '<actual-source-reader-owned-gate>', 'exec')
        identity, label = "a" * 64, "b" * 32
        for inspected in (SimpleNamespace(returncode=1, stdout=""),
                SimpleNamespace(returncode=0, stdout=identity + "|wrong"),
                SimpleNamespace(returncode=0, stdout="g" * 64 + "|" + label)):
            calls = []
            def run(arguments, **kwargs):
                calls.append(arguments)
                self.assertEqual(["docker", "inspect"], arguments[:2])
                return inspected
            namespace = {"subprocess": SimpleNamespace(run=run), "re": re, "name": "aioffice-owned-source-reader", "suffix": label}
            exec(compiled, namespace)
            if inspected.returncode:
                self.assertFalse(namespace["source_reader_awaiting"]())
                with self.assertRaises(AssertionError): namespace["release_source_reader"]()
            else:
                with self.assertRaises(AssertionError): namespace["source_reader_awaiting"]()
                with self.assertRaises(AssertionError): namespace["release_source_reader"]()
            self.assertEqual(2, len(calls))

    def test_source_key_release_reinspects_identity_and_requires_exact_terminal_reply(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name in
            ("source_reader_container", "release_source_reader")]
        compiled = compile(ast.Module(body=functions, type_ignores=[]), '<actual-source-reader-release>', 'exec')
        identity, label = "a" * 64, "b" * 32
        for fault in (None, "release", "reply"):
            calls = []
            def run(arguments, **kwargs):
                calls.append(arguments)
                if arguments[:2] == ["docker", "inspect"]:
                    return SimpleNamespace(returncode=0, stdout=identity + "|" + label)
                self.assertEqual(["docker", "exec", identity, "sh", "-c", ": > /tmp/aioffice-source-proof-release"], arguments)
                return SimpleNamespace(returncode=1 if fault == "release" else 0)
            held = SimpleNamespace(returncode=0, communicate=lambda **kwargs: ("unexpected" if fault == "reply" else
                "CHECKPOINT owned source key resolved outside SQL before final fence\n"
                "PASS owned source runtime current Extract revocation during key await denies private context before decrypt\n", ""))
            namespace = {"subprocess": SimpleNamespace(run=run), "re": re, "name": "aioffice-owned-source-reader", "suffix": label, "hold": held}
            exec(compiled, namespace)
            if fault:
                with self.assertRaises(AssertionError): namespace["release_source_reader"]()
            else:
                namespace["release_source_reader"]()
            self.assertEqual(["docker", "inspect"], calls[0][:2]); self.assertEqual(2, len(calls))

    root = (Path.cwd() / "guard-test-no-resources").resolve()
    owned = root / "aioffice-local"
    environment = {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": str(root)}

    def test_managed_recovery_refuses_unowned_before_credentials_files_sql_or_processes(self):
        for directory, override, api in [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080"),
                (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080"),
                (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080"),
                (self.root, {}, "http://127.0.0.1:8080"), (self.owned / "nested", {}, "http://127.0.0.1:8080"),
                (self.owned, {}, "https://foreign.invalid")]:
            with self.subTest(override=override), patch.dict(os.environ, {**self.environment, **override}), \
                    patch.object(recovery_smoke.subprocess, "run") as process, patch.object(Path, "mkdir") as directory_create, \
                    patch.object(recovery_smoke.secrets, "token_bytes") as key:
                sql = lambda *args: self.fail("Unowned recovery touched SQL")
                with self.assertRaisesRegex(RuntimeError, "owned disposable GitHub CI fixture"):
                    recovery_smoke.verify(directory=directory, api=api, tenant="INVALID", company="INVALID", service="INVALID",
                        key=None, runtime_password=None, sql=sql, restart=sql, ready=sql, identity_index=sql, enroll_source=sql)
                process.assert_not_called(); directory_create.assert_not_called(); key.assert_not_called()

    def test_managed_recovery_cleanup_attempts_each_owned_resource_and_retains_first_failure(self):
        tree = ast.parse(Path(recovery_smoke.__file__).read_text(encoding="utf-8"))
        function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        boundary = next(node for node in function.body if isinstance(node, ast.Try) and node.finalbody)
        wrapper = ast.FunctionDef(name="cleanup", args=ast.arguments(posonlyargs=[], args=[ast.arg(arg="failure")],
            kwonlyargs=[], kw_defaults=[], defaults=[]), body=boundary.finalbody + [ast.Return(value=ast.Name(id="failure", ctx=ast.Load()))],
            decorator_list=[])
        block = compile(ast.fix_missing_locations(ast.Module(body=[wrapper], type_ignores=[])), '<actual-managed-cleanup>', 'exec')
        for fault in (None, "poll", "kill", "drain", "timeout", "timeout-kill", "timeout-drain", "shutdown", "close", "join"):
            for earlier in (False, True):
                effects = []; first = RuntimeError("earlier-owned-failure") if earlier else None
                injected = RuntimeError(fault) if fault is not None else None
                timeout = recovery_smoke.subprocess.TimeoutExpired("owned-child", 5)
                drain_calls = kill_calls = 0
                def effect(name):
                    effects.append(name)
                    if name == fault: raise injected
                def poll():
                    effect("poll"); return None
                def kill():
                    nonlocal kill_calls
                    kill_calls += 1; effect("kill")
                    if fault == "timeout-kill" and kill_calls == 2: raise injected
                def communicate(*, timeout):
                    nonlocal drain_calls
                    self.assertEqual(5, timeout)
                    drain_calls += 1; effect("drain")
                    if fault in ("timeout", "timeout-kill", "timeout-drain"):
                        if drain_calls == 1: raise namespace['owned_timeout']
                        if fault == "timeout-drain": raise injected
                process = SimpleNamespace(poll=poll, kill=kill, communicate=communicate)
                proxy = SimpleNamespace(shutdown=lambda: effect("shutdown"), server_close=lambda: effect("close"))
                serving = SimpleNamespace(is_alive=lambda: True, ident=1, join=lambda **kwargs: effect("join"))
                namespace = dict(process=process, proxy=proxy, serving=serving, subprocess=recovery_smoke.subprocess,
                    owned_timeout=timeout, release=SimpleNamespace(set=lambda: effect("release")))
                exec(block, namespace)
                result = namespace['cleanup'](first)
                self.assertEqual(["shutdown", "close", "join"], effects[-3:])
                self.assertIn("kill", effects)
                self.assertIn("drain", effects)
                self.assertEqual(2 if fault in ("timeout", "timeout-kill", "timeout-drain") else 1, drain_calls)
                self.assertEqual(2 if fault in ("timeout", "timeout-kill", "timeout-drain") else 1, kill_calls)
                if earlier: self.assertIs(first, result)
                else: self.assertIs(timeout if fault in ("timeout", "timeout-kill", "timeout-drain") else injected, result)

    def test_reference_refuses_unowned_before_configuration_sql_or_processes(self):
        for directory, override, api in [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080"),
                (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080"),
                (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080"),
                (self.owned / "nested", {}, "http://127.0.0.1:8080"), (self.root, {}, "http://127.0.0.1:8080"),
                (self.owned, {}, "https://customer.example.invalid")]:
            with self.subTest(directory=str(directory), override=override, api=api), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with patch.object(reference_smoke.subprocess, "run") as process:
                    with self.assertRaisesRegex(RuntimeError, r"^Group reference proof requires the owned disposable GitHub CI fixture\.$"):
                        reference_smoke.verify(directory=directory, api=api, manifest=None, tenant=None, company=None,
                            service=None, source=None, sql=None, compose=None, environment=None, pipeline=None)
                    process.assert_not_called()

    def test_reference_pipeline_actual_disable_attempts_worker_even_when_core_restore_fails(self):
        tree = ast.parse(Path(group_smoke.__file__).read_text(encoding="utf-8"))
        function = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "reference_pipeline")
        block = compile(ast.Module(body=[function], type_ignores=[]), '<actual-reference-pipeline>', 'exec')
        for failure in ("write", "chmod", "core-api", "agent-worker", "write-and-worker", None):
            calls = []; ready_calls = []
            class Override:
                def write_text(self, value, encoding):
                    self.value = json.loads(value)
                    if failure in ("write", "write-and-worker"): raise RuntimeError("write")
                def chmod(self, mode):
                    self.mode = mode
                    if failure == "chmod": raise RuntimeError("chmod")
            override = Override()
            def compose_run(*args, **kwargs):
                calls.append((args, kwargs))
                if failure in args or failure == "write-and-worker" and "agent-worker" in args:
                    raise RuntimeError(args[-1])
            namespace = dict(json=json, tenant=str(uuid.uuid4()), company=str(uuid.uuid4()), service=str(uuid.uuid4()),
                private_environment={"OWNED_SECRET": "private-inert", "AIOffice__GroupIntake__Enabled": "true"},
                override=override, compose_run=compose_run, ready=lambda: ready_calls.append("ready"))
            exec(block, namespace)
            if failure:
                with self.assertRaisesRegex(RuntimeError, '^' + ("write" if failure == "write-and-worker" else failure) + '$'):
                    namespace['reference_pipeline']('disable')
            else: namespace['reference_pipeline']('disable')
            self.assertEqual(['agent-worker'] if failure in ("write", "chmod", "write-and-worker") else ['core-api', 'agent-worker'],
                [args[-1] for args, _ in calls])
            self.assertFalse(calls[-1][1])
            if len(calls) == 2: self.assertTrue(calls[0][1]['overridden'])
            self.assertEqual([] if failure else ["ready"], ready_calls)
            self.assertEqual({'services': {'core-api': {'environment': namespace['private_environment']}}}, override.value)
            self.assertNotIn('AIOffice__GroupIntake__PipelineEnabled', override.value['services']['core-api']['environment'])

    def test_reference_temporary_sql_owns_lost_setup_reply_and_independent_restoration(self):
        class InjectedFailure(Exception): pass
        for fault in (None, "setup", "action", "restore-first", "restore-second", "setup-and-restore"):
            with self.subTest(fault=fault):
                effects = []
                def sql(statement):
                    effects.append(statement)
                    if statement == "setup" and fault in ("setup", "setup-and-restore"): raise InjectedFailure("setup")
                    if statement == "restore-first" and fault in ("restore-first", "setup-and-restore"): raise InjectedFailure("restore-first")
                    if statement == "restore-second" and fault == "restore-second": raise InjectedFailure("restore-second")
                def action():
                    effects.append("action")
                    if fault == "action": raise InjectedFailure("action")
                if fault:
                    with self.assertRaises(InjectedFailure) as failure:
                        reference_smoke.temporary_sql(sql, "setup", ["restore-first", "restore-second"], action)
                    self.assertEqual("setup" if fault == "setup-and-restore" else fault, str(failure.exception))
                else: reference_smoke.temporary_sql(sql, "setup", ["restore-first", "restore-second"], action)
                self.assertEqual(["restore-first", "restore-second"], effects[-2:])
                self.assertEqual(fault not in ("setup", "setup-and-restore"), "action" in effects)

    def test_reference_queued_gate_cleanup_survives_every_setup_and_drain_boundary(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        function = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "queued_extract_revoke")
        block = compile(ast.Module(body=[function], type_ignores=[]), '<actual-reference-lock-cleanup>', 'exec')
        class InjectedFailure(Exception): pass
        for fault in (None, "create", "popen", "stdin-write", "stdin-close", "executor", "observation", "pending",
                "release", "drain", "drain-and-kill", "shutdown", "restore", "drop", "observation-and-restore"):
            with self.subTest(fault=fault):
                effects = []; drain_calls = 0
                def effect(name):
                    effects.append(name)
                    if fault == name or fault == "observation-and-restore" and name in ("observation", "restore"):
                        raise InjectedFailure(name)
                class Input:
                    def write(self, value): effect("stdin-write")
                    def close(self): effect("stdin-close")
                class Locker:
                    stdin = Input(); returncode = 0
                    def communicate(self, timeout):
                        nonlocal drain_calls
                        drain_calls += 1; effects.append("drain")
                        if drain_calls == 1 and fault in ("drain", "drain-and-kill"): raise InjectedFailure("drain")
                        return ("", "")
                    def kill(self):
                        effects.append("kill")
                        if fault == "drain-and-kill": raise InjectedFailure("kill")
                class Pending:
                    def result(self, timeout): effect("pending")
                class Executor:
                    def __init__(self, max_workers): effect("executor")
                    def submit(self, *args): effect("submit"); return Pending()
                    def shutdown(self, **kwargs): effect("shutdown")
                def popen(*args, **kwargs): effect("popen"); return Locker()
                def sql(query):
                    if query.startswith("CREATE TABLE"): effect("create"); return ""
                    if query.startswith("IF OBJECT_ID") and "UPDATE" in query: effect("release"); return ""
                    if query.startswith("IF OBJECT_ID") and "DROP" in query: effect("drop"); return ""
                    if "SET IsEnabled=1" in query: effect("restore"); return ""
                    if "SET IsEnabled=0" in query: effect("revoke"); return ""
                    if "sys.dm_tran_locks" in query: effect("observation"); return "1"
                    self.fail("Unexpected owned inert lock SQL")
                namespace = dict(sql=sql, suffix="a" * 32, scope="owned-scope", grant="owned-grant", compose=["owned-compose"],
                    environment={}, subprocess=SimpleNamespace(Popen=popen, PIPE=None), ThreadPoolExecutor=Executor,
                    wait=lambda predicate, seconds: self.assertTrue(predicate()), run=lambda *args: None)
                exec(block, namespace)
                if fault:
                    with self.assertRaises(InjectedFailure) as failure: namespace['queued_extract_revoke']()
                    expected = "drain" if fault == "drain-and-kill" else "observation" if fault == "observation-and-restore" else fault
                    self.assertEqual(expected, str(failure.exception))
                else: namespace['queued_extract_revoke']()
                self.assertIn("release", effects); self.assertIn("restore", effects); self.assertIn("drop", effects)
                if fault not in ("create", "popen"): self.assertIn("drain", effects)
                if fault not in ("create", "popen", "stdin-write", "stdin-close", "executor"): self.assertIn("shutdown", effects)
                if fault in ("drain", "drain-and-kill"):
                    self.assertIn("kill", effects); self.assertEqual(2, drain_calls)

    def test_reference_restart_requires_new_broker_delivery_and_ack_with_same_full_sql_graph(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        attempt = next(node for node in verify.body if isinstance(node, ast.Try) and any(isinstance(child, ast.FunctionDef)
            and child.name == "unsafe_column" for child in node.body))
        start = next(i for i, node in enumerate(attempt.body) if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name) and node.targets[0].id == "stable") + 1
        end = next(i for i in range(start, len(attempt.body)) if isinstance(attempt.body[i], ast.Assert)
            and isinstance(attempt.body[i].test, ast.BoolOp) and "full_graph" in ast.unparse(attempt.body[i])) + 1
        block = compile(ast.Module(body=attempt.body[start:end], type_ignores=[]), '<actual-reference-restart-oracle>', 'exec')
        for fault in (None, "no-consumer", "dead-consumer", "no-publication", "wrong-deliver", "pending-queue", "changed-receipt"):
            with self.subTest(fault=fault):
                calls = []; state = {"restarted": False, "published": False}
                def pipeline(operation): calls.append(operation); state["restarted"] = True
                def run(mode): calls.append(mode); state["published"] = True
                def stats():
                    resumed = state["published"] and fault not in ("dead-consumer", "no-publication")
                    return {"ack": 3 if resumed else 2, "deliver": 4 if resumed and fault != "wrong-deliver" else 3,
                        "consumers": 0 if state["restarted"] and fault == "no-consumer" else 1}
                namespace = dict(wait=lambda predicate: self.assertTrue(predicate()), broker_stats=stats, pipeline=pipeline, run=run,
                    wait_reference_statistics=reference_smoke.wait_reference_statistics,
                    stable=["same-receipt"], count=lambda: 2, full_graph=lambda: ["changed-receipt" if fault == "changed-receipt" else "same-receipt"],
                    queue_counts=lambda: (1, 0) if fault == "pending-queue" else (0, 0))
                if fault:
                    with self.assertRaises(AssertionError): exec(block, namespace)
                else:
                    exec(block, namespace); self.assertEqual(["restart", "publish-existing"], calls)

    def test_application_restart_observes_exact_baseline_and_delivery_in_one_snapshot(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        attempt = next(node for node in verify.body if isinstance(node, ast.Try) and any(isinstance(child, ast.FunctionDef)
            and child.name == "unsafe_column" for child in node.body))
        start = next(i for i, node in enumerate(attempt.body) if isinstance(node, ast.Assign)
            and isinstance(node.targets[0], ast.Name) and node.targets[0].id == "stable") + 1
        end = next(i for i in range(start, len(attempt.body)) if isinstance(attempt.body[i], ast.Assert)
            and isinstance(attempt.body[i].test, ast.BoolOp) and "full_graph" in ast.unparse(attempt.body[i])) + 1
        block = compile(ast.Module(body=attempt.body[start:end], type_ignores=[]), '<actual-application-restart-statistics>', 'exec')
        cases = (
            ("delayed", True, [(0, -1, 0), (0, 0, 0), (0, 0, 1)]),
            ("disjoint", False, [(0, -1, 1), (-1, 0, 1), (0, -1, 1)]),
            ("extra-ack", False, [(0, -1, 1), (1, 0, 1), (1, 0, 1)]),
            ("extra-delivery", False, [(0, 1, 1)] * 3),
            ("missing-consumer", False, [(0, 0, 0)] * 3),
            ("extra-consumer", False, [(0, 0, 2)] * 3),
        )
        for phase in ("before", "after"):
            for case, succeeds, frames in cases:
                with self.subTest(phase=phase, case=case):
                    state = dict(published=False, restarted=False, observations=0)
                    snapshots = iter(frames)
                    calls = []
                    def pipeline(operation):
                        calls.append(operation); state["restarted"] = True
                    def run(mode):
                        calls.append(mode); state["published"] = True
                    def stats():
                        selected = phase == "before" and not state["restarted"] or phase == "after" and state["published"]
                        if not selected:
                            return dict(ack=3 if state["published"] else 2, deliver=4 if state["published"] else 3, consumers=1)
                        state["observations"] += 1
                        ack_offset, delivery_offset, consumers = next(snapshots)
                        return dict(ack=(3 if state["published"] else 2) + ack_offset,
                            deliver=(4 if state["published"] else 3) + delivery_offset, consumers=consumers)
                    def bounded_wait(predicate, seconds=30):
                        self.assertEqual(seconds, 30)
                        for _ in range(3):
                            if predicate(): return
                        raise AssertionError("Owned observation deadline exceeded")
                    namespace = dict(wait=bounded_wait, broker_stats=stats, pipeline=pipeline, run=run,
                        wait_reference_statistics=reference_smoke.wait_reference_statistics, stable=["unchanged-full8"],
                        count=lambda: 2, full_graph=lambda: ["unchanged-full8"], queue_counts=lambda: (0, 0))
                    if succeeds:
                        exec(block, namespace)
                        self.assertEqual(calls, ["restart", "publish-existing"])
                    else:
                        with self.assertRaisesRegex(AssertionError, f"Owned reference {phase} application restart observation failed") as raised:
                            exec(block, namespace)
                        self.assertEqual(str(raised.exception.__cause__), "Owned observation deadline exceeded")
                        self.assertIn("expected_ack=", str(raised.exception))
                        self.assertIn("expected_deliver=", str(raised.exception))
                        self.assertEqual(calls, [] if phase == "before" else ["restart", "publish-existing"])
                    self.assertEqual(state["observations"], 3)

    def test_pending_broker_restart_refuses_unowned_before_callbacks(self):
        def forbidden(*args): raise AssertionError("Owned guard touched resources")
        with patch.dict(os.environ, {**self.environment, "CI": "false"}, clear=True):
            with self.assertRaisesRegex(RuntimeError, "requires the owned disposable GitHub CI fixture"):
                reference_smoke.verify_pending_broker_restart(directory=self.owned, api="http://127.0.0.1:8080",
                    pause_worker=forbidden, resume_worker=forbidden, broker_state=forbidden,
                    restart_broker=forbidden, queue_counts=forbidden, broker_stats=forbidden,
                    publish=forbidden, inspect_pending=forbidden, full_graph=forbidden, count=forbidden, wait=forbidden)

    def test_pending_broker_restart_original_reference_survives_before_shipping_ack(self):
        faults = (None, "stop-reply-lost", "restart-failed", "different-broker", "same-incarnation", "unhealthy",
            "lost-message", "unexpected-consumer", "graph-after-pause", "graph-after-publish", "graph-after-restart",
            "graph-after-resume", "duplicate-effect", "no-ack", "wrong-delivery", "pending-after-ack", "resume-failed",
            "wrong-original-reference")
        for fault in faults:
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True):
                calls = []
                state = dict(paused=False, published=False, restarted=False, resumed=False)
                first = RuntimeError("Owned first operation failed")
                def pause():
                    calls.append("pause"); state["paused"] = True
                    if fault == "stop-reply-lost": raise first
                def resume():
                    calls.append("resume"); state["resumed"] = True
                    if fault == "resume-failed": raise first
                def publish():
                    self.assertTrue(state["paused"]); self.assertFalse(state["restarted"])
                    calls.append("publish"); state["published"] = True
                def restart(identity):
                    self.assertEqual(identity, "owned-broker"); self.assertTrue(state["published"])
                    calls.append("restart"); state["restarted"] = True
                    if fault == "restart-failed": raise first
                def inspect_pending():
                    self.assertTrue(state["published"]); self.assertFalse(state["resumed"])
                    calls.append("inspect")
                    if state["restarted"] and fault == "wrong-original-reference": raise AssertionError("Original pending reference changed")
                def broker():
                    return ("different" if state["restarted"] and fault == "different-broker" else "owned-broker",
                        2 if state["restarted"] and fault != "same-incarnation" else 1,
                        not (state["restarted"] and fault == "unhealthy"))
                def queue():
                    if not state["published"]: return (0, 0)
                    if state["resumed"]: return (1, 0) if fault == "pending-after-ack" else (0, 0)
                    return (0, 0) if state["restarted"] and fault == "lost-message" else (1, 0)
                def statistics():
                    resumed = state["resumed"]
                    # A fresh process may reset counters; require positive deltas.
                    return dict(ack=11 if resumed and fault != "no-ack" else 10,
                        deliver=22 if resumed and fault == "wrong-delivery" else 21 if resumed else 20,
                        consumers=1 if resumed or state["restarted"] and fault == "unexpected-consumer" else 0)
                def graph():
                    changed = any(state[phase] and fault == label for phase, label in (
                        ("paused", "graph-after-pause"), ("published", "graph-after-publish"),
                        ("restarted", "graph-after-restart"), ("resumed", "graph-after-resume")))
                    return ["changed" if changed else "original-eight-table-graph"]
                arguments = dict(directory=self.owned, api="http://127.0.0.1:8080", pause_worker=pause,
                    resume_worker=resume, broker_state=broker, restart_broker=restart, queue_counts=queue,
                    broker_stats=statistics, publish=publish, inspect_pending=inspect_pending, full_graph=graph,
                    count=lambda: 3 if state["resumed"] and fault == "duplicate-effect" else 2,
                    wait=lambda predicate, seconds=30: self.assertTrue(predicate()))
                if fault:
                    with self.assertRaises((AssertionError, RuntimeError)) as raised:
                        reference_smoke.verify_pending_broker_restart(**arguments)
                    if fault in ("stop-reply-lost", "restart-failed", "resume-failed"): self.assertIs(raised.exception, first)
                else: reference_smoke.verify_pending_broker_restart(**arguments)
                self.assertEqual(calls.count("resume"), 1)
                if not fault: self.assertEqual(calls, ["pause", "publish", "inspect", "restart", "inspect", "resume"])

    def test_pending_broker_restart_observes_all_exact_signals_together_within_existing_bound(self):
        sequences = (
            ("delayed", True, [(11, 20, 0), (11, 21, 0), (11, 21, 1)], None),
            ("disjoint", False, [(11, 20, 1), (10, 21, 1), (11, 20, 1)], "ack_delta=1, deliver_delta=0, consumers=1"),
            ("overshoot", False, [(12, 22, 1)] * 3, "ack_delta=2, deliver_delta=2, consumers=1"),
            ("ack-changed-between-samples", False, [(11, 20, 0), (12, 21, 1), (12, 21, 1)], "ack_delta=2, deliver_delta=1, consumers=1"),
            ("missing-consumer", False, [(11, 21, 0)] * 3, "ack_delta=1, deliver_delta=1, consumers=0"),
            ("extra-consumer", False, [(11, 21, 2)] * 3, "ack_delta=1, deliver_delta=1, consumers=2"),
        )
        for case, succeeds, snapshots, diagnostic in sequences:
            with self.subTest(case=case), patch.dict(os.environ, self.environment, clear=True):
                state = dict(published=False, restarted=False, resumed=False, observations=0)
                frames = iter(snapshots)
                def publish():
                    self.assertFalse(state["restarted"]); state["published"] = True
                def restart(identity):
                    self.assertEqual(identity, "owned"); state["restarted"] = True
                def resume(): state["resumed"] = True
                def statistics():
                    if not state["resumed"]: return dict(ack=10, deliver=20, consumers=0)
                    state["observations"] += 1
                    ack, deliver, consumers = next(frames)
                    return dict(ack=ack, deliver=deliver, consumers=consumers)
                def bounded_wait(predicate, seconds=30):
                    self.assertIn(seconds, (30, 60))
                    for _ in range(3):
                        if predicate(): return
                    raise AssertionError("Owned reference observation deadline exceeded")
                arguments = dict(directory=self.owned, api="http://127.0.0.1:8080", pause_worker=lambda: None,
                    resume_worker=resume, broker_state=lambda: ("owned", 2 if state["restarted"] else 1, True),
                    restart_broker=restart, queue_counts=lambda: (1, 0) if state["published"] and not state["resumed"] else (0, 0),
                    broker_stats=statistics, publish=publish, inspect_pending=lambda: None,
                    full_graph=lambda: ["original-eight-table-graph"], count=lambda: 2, wait=bounded_wait)
                if succeeds:
                    reference_smoke.verify_pending_broker_restart(**arguments)
                else:
                    with self.assertRaisesRegex(AssertionError, diagnostic) as raised:
                        reference_smoke.verify_pending_broker_restart(**arguments)
                    self.assertEqual(str(raised.exception.__cause__), "Owned reference observation deadline exceeded")
                self.assertEqual(state["observations"], 3)

    def test_pending_broker_restart_preserves_stop_failure_when_restoration_also_fails(self):
        first = RuntimeError("Owned stop reply lost")
        calls = []
        def stop(): calls.append("stop"); raise first
        def start(): calls.append("start"); raise RuntimeError("Owned restore failed")
        def forbidden(*args): raise AssertionError("Later operation ran after failure")
        with patch.dict(os.environ, self.environment, clear=True):
            with self.assertRaises(RuntimeError) as raised:
                reference_smoke.verify_pending_broker_restart(directory=self.owned, api="http://127.0.0.1:8080",
                    pause_worker=stop, resume_worker=start, broker_state=lambda: ("owned", 1, True),
                    restart_broker=forbidden, queue_counts=lambda: (0, 0), broker_stats=forbidden,
                    publish=forbidden, inspect_pending=forbidden, full_graph=lambda: ["unchanged"], count=lambda: 2, wait=forbidden)
        self.assertIs(raised.exception, first); self.assertEqual(calls, ["stop", "start"])

    def test_spool_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, "http://127.0.0.1:8080", {"CI": "false"}),
            (self.owned, "http://127.0.0.1:8080", {"GITHUB_ACTIONS": "false"}),
            (self.owned, "http://127.0.0.1:8080", {"RUNNER_TEMP": ""}),
            (self.root, "http://127.0.0.1:8080", {}), (self.owned / "nested", "http://127.0.0.1:8080", {}),
            (self.owned, "https://production.invalid/", {})]
        def forbidden(*args, **kwargs): self.fail("Unowned spool proof touched resources")
        for directory, api, override in cases:
            with self.subTest(directory=str(directory), override=override), patch.dict(os.environ, {**self.environment, **override}, clear=True), \
                    patch.object(spool_smoke.subprocess, "run", forbidden), patch.object(spool_smoke.subprocess, "Popen", forbidden), \
                    patch.object(spool_smoke.http.server, "ThreadingHTTPServer", forbidden), patch.object(Path, "mkdir", forbidden):
                with self.assertRaisesRegex(RuntimeError, "^Spool proof requires the owned disposable GitHub CI fixture\\.$"):
                    spool_smoke.verify(directory=directory, api=api, tenant="invalid", company="invalid", service="invalid",
                        key=None, sql=forbidden, restart=forbidden, ready=forbidden, identity_index=forbidden)

    def test_spool_proof_validates_owned_key_before_files_or_sql(self):
        def forbidden(*args, **kwargs): self.fail("Invalid proof key touched resources")
        with patch.dict(os.environ, self.environment, clear=True), patch.object(Path, "mkdir", forbidden):
            with self.assertRaisesRegex(RuntimeError, "^Owned spool signing key is unavailable\\.$"):
                spool_smoke.verify(directory=self.owned, api="http://127.0.0.1:8080", tenant=str(uuid.uuid4()),
                    company=str(uuid.uuid4()), service=str(uuid.uuid4()), key=b"bad",
                    sql=forbidden, restart=forbidden, ready=forbidden, identity_index=forbidden)

    def test_spool_proof_requires_owned_source_key_enrollment_before_resources(self):
        def forbidden(*args, **kwargs): self.fail("Missing source key enrollment touched resources")
        with patch.dict(os.environ, self.environment, clear=True), patch.object(Path, "mkdir", forbidden):
            with self.assertRaisesRegex(RuntimeError, "^Owned spool source key enrollment is unavailable\\.$"):
                spool_smoke.verify(directory=self.owned, api="http://127.0.0.1:8080", tenant=str(uuid.uuid4()),
                    company=str(uuid.uuid4()), service=str(uuid.uuid4()), key=b"x"*32,
                    sql=forbidden, restart=forbidden, ready=forbidden, identity_index=forbidden)

    def test_native_source_key_callback_preserves_original_enrollment_and_recreates_only_owned_core(self):
        tree = ast.parse(Path(group_smoke.__file__).read_text(encoding="utf-8"))
        callback = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "enroll_owned_source")
        block = compile(ast.fix_missing_locations(ast.Module([callback], type_ignores=[])), "<inert-source-key-enrollment>", "exec")
        calls = []
        class Override:
            def write_text(self, text, encoding): calls.append(("write", json.loads(text), encoding))
            def chmod(self, mode): calls.append(("chmod", mode))
        original = {"AIOffice__GroupIntake__SourceKeys__0__SourceBindingId": str(uuid.uuid4()),
            "AIOffice__GroupIntake__SourceKeys__0__SecretRef": "secretref://env/INERT_ORIGINAL_CONTENT_KEY",
            "INERT_ORIGINAL_CONTENT_KEY": "owned-inert-original-value"}
        environment = original.copy()
        tenant, company, source = (str(uuid.uuid4()) for _ in range(3))
        namespace = dict(uuid=uuid, base64=base64, json=json, secrets=SimpleNamespace(token_bytes=lambda size: b"n"*size),
            tenant=tenant, company=company, private_environment=environment, override=Override(),
            compose_run=lambda *args, **kwargs: calls.append(("compose", args, kwargs)), ready=lambda: calls.append(("ready",)))
        exec(block, namespace)
        with self.assertRaises(ValueError): namespace["enroll_owned_source"]("invalid-source", 1)
        for slot in (0, 9, True, "1"):
            with self.assertRaises(AssertionError): namespace["enroll_owned_source"](source, slot)
        self.assertEqual(original, environment); self.assertEqual([], calls)
        namespace["enroll_owned_source"](source, 1)
        self.assertTrue(all(environment[name] == value for name, value in original.items()))
        prefix = "AIOffice__GroupIntake__SourceKeys__1__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_NATIVE_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(prefix):]: value for name, value in environment.items() if name.startswith(prefix)})
        self.assertEqual(environment, calls[0][1]["services"]["core-api"]["environment"])
        self.assertEqual(("chmod", 0o600), calls[1])
        self.assertEqual(("compose", ("up", "-d", "--no-deps", "--force-recreate", "core-api"), {"overridden": True}), calls[2])
        self.assertEqual(("ready",), calls[3])
        before = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](source, 1)
        self.assertEqual(before, environment); self.assertEqual(4, len(calls))
        second_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](second_source, 2)
        self.assertTrue(all(environment[name] == value for name, value in before.items()))
        second_prefix = "AIOffice__GroupIntake__SourceKeys__2__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": second_source,
            "KeyId": "owned-managed-source-v1", "SecretRef": "secretref://env/OWNED_MANAGED_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(second_prefix):]: value for name, value in environment.items() if name.startswith(second_prefix)})
        self.assertEqual(before["OWNED_NATIVE_GROUP_CONTENT_KEY"], environment["OWNED_NATIVE_GROUP_CONTENT_KEY"])
        self.assertEqual(["write", "chmod", "compose", "ready"], [call[0] for call in calls[4:]])
        final = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](second_source, 2)
        self.assertEqual(final, environment); self.assertEqual(8, len(calls))
        third_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](third_source, 3)
        self.assertTrue(all(environment[name] == value for name, value in final.items()))
        third_prefix = "AIOffice__GroupIntake__SourceKeys__3__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": third_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_EFFECT_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(third_prefix):]: value for name, value in environment.items() if name.startswith(third_prefix)})
        last = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](third_source, 3)
        self.assertEqual(last, environment); self.assertEqual(12, len(calls))
        fourth_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](fourth_source, 4)
        self.assertTrue(all(environment[name] == value for name, value in last.items()))
        fourth_prefix = "AIOffice__GroupIntake__SourceKeys__4__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": fourth_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_AUTOMATIC_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(fourth_prefix):]: value for name, value in environment.items() if name.startswith(fourth_prefix)})
        final_automatic = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](fourth_source, 4)
        self.assertEqual(final_automatic, environment); self.assertEqual(16, len(calls))
        fifth_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](fifth_source, 5)
        self.assertTrue(all(environment[name] == value for name, value in final_automatic.items()))
        fifth_prefix = "AIOffice__GroupIntake__SourceKeys__5__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": fifth_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_NO_WORK_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(fifth_prefix):]: value for name, value in environment.items() if name.startswith(fifth_prefix)})
        final_no_work = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](fifth_source, 5)
        self.assertEqual(final_no_work, environment); self.assertEqual(20, len(calls))
        sixth_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](sixth_source, 6)
        self.assertTrue(all(environment[name] == value for name, value in final_no_work.items()))
        sixth_prefix = "AIOffice__GroupIntake__SourceKeys__6__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": sixth_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_HOST_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(sixth_prefix):]: value for name, value in environment.items() if name.startswith(sixth_prefix)})
        final_host = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](sixth_source, 6)
        self.assertEqual(final_host, environment); self.assertEqual(24, len(calls))
        seventh_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](seventh_source, 7)
        self.assertTrue(all(environment[name] == value for name, value in final_host.items()))
        seventh_prefix = "AIOffice__GroupIntake__SourceKeys__7__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": seventh_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_COVERAGE_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(seventh_prefix):]: value for name, value in environment.items() if name.startswith(seventh_prefix)})
        final_coverage = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](seventh_source, 7)
        self.assertEqual(final_coverage, environment); self.assertEqual(28, len(calls))
        eighth_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](eighth_source, 8)
        self.assertTrue(all(environment[name] == value for name, value in final_coverage.items()))
        eighth_prefix = "AIOffice__GroupIntake__SourceKeys__8__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": eighth_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_RAW_HISTORY_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(eighth_prefix):]: value for name, value in environment.items() if name.startswith(eighth_prefix)})
        final_raw_history = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](eighth_source, 8)
        self.assertEqual(final_raw_history, environment); self.assertEqual(32, len(calls))

    def test_spool_proxy_partial_startup_closes_allocated_resources_and_preserves_first_error(self):
        # Execute only the actual cleanup block with inert resources: no CI
        # flags, sockets, child processes, private configuration or SQL.
        tree = ast.parse(Path(spool_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        start = next(index for index, node in enumerate(verify.body) if isinstance(node, ast.Assign)
            and any(isinstance(target, ast.Name) and target.id == "committed" for target in node.targets))
        end = next(index for index in range(start, len(verify.body)) if isinstance(verify.body[index], ast.If)
            and isinstance(verify.body[index].test, ast.Compare) and isinstance(verify.body[index].test.left, ast.Name)
            and verify.body[index].test.left.id == "failure")
        block = compile(ast.fix_missing_locations(ast.Module(verify.body[start:end + 1], type_ignores=[])), "<inert-spool-cleanup>", "exec")
        for fault in ("proxy-constructor", "thread-constructor", "thread-start", "save", "popen", "drain"):
            with self.subTest(fault=fault):
                actions, namespace = [], {}
                original = RuntimeError("owned-first-failure")
                class Event:
                    def set(self): actions.append("release")
                    def wait(self, timeout): return True
                class Proxy:
                    server_port = 12345
                    def __init__(self, *args):
                        actions.append("proxy")
                        if fault == "proxy-constructor": raise original
                    def serve_forever(self): pass
                    def shutdown(self): actions.append("shutdown")
                    def server_close(self):
                        actions.append("close")
                        # A later cleanup error must not hide the first fault.
                        raise RuntimeError("owned-secondary-cleanup-failure")
                class Thread:
                    ident = None
                    alive = False
                    def __init__(self, *args, **kwargs):
                        actions.append("thread")
                        if fault == "thread-constructor": raise original
                    def start(self):
                        self.ident, self.alive = 1, True
                        namespace["captured"]["ack"] = {}
                        if fault == "thread-start": raise original
                    def is_alive(self): return self.alive
                    def join(self, timeout): actions.append("join")
                class Process:
                    returncode = None
                    def __init__(self, *args, **kwargs):
                        actions.append("popen")
                        if fault == "popen": raise original
                    def poll(self): return self.returncode
                    def kill(self): actions.append("kill"); self.returncode = -9
                    def communicate(self, timeout):
                        actions.append("drain")
                        if fault == "drain" and actions.count("drain") == 1: raise original
                def save():
                    if fault == "save": raise original
                namespace.update(threading=SimpleNamespace(Event=Event, Thread=Thread),
                    http=SimpleNamespace(server=SimpleNamespace(BaseHTTPRequestHandler=object, ThreadingHTTPServer=Proxy)),
                    subprocess=SimpleNamespace(Popen=Process, PIPE=None), config={}, save_config=save,
                    executable="inert-never-executed.dll", child_environment={})
                with self.assertRaises(RuntimeError) as caught:
                    exec(block, namespace)
                self.assertIs(caught.exception, original)
                self.assertIn("release", actions)
                if fault != "proxy-constructor": self.assertIn("close", actions)
                if fault not in ("proxy-constructor", "thread-constructor"):
                    self.assertIn("shutdown", actions)
                    self.assertIn("join", actions)
                if fault == "drain": self.assertEqual(2, actions.count("drain"))

    def test_spool_proxy_forwards_actual_metadata_and_lease_bytes_but_only_withholds_event_ack(self):
        tree = ast.parse(Path(spool_smoke.__file__).read_text(encoding="utf-8"))
        handler = next(node for node in ast.walk(tree) if isinstance(node, ast.ClassDef) and node.name == "LostReply")
        block = compile(ast.fix_missing_locations(ast.Module([handler], type_ignores=[])), "<inert-spool-proxy>", "exec")
        tenant, company, source = (str(uuid.uuid4()) for _ in range(3))
        for path in ("enrollment", "listener", "events"):
            with self.subTest(path=path):
                actions, captured = [], {}
                raw = json.dumps({"source": {"tenantId": tenant, "companyId": company, "sourceBindingId": source},
                    "owned-inert-body": "\u00e9"}, separators=(",", ":")).encode()
                class Response:
                    status = 200
                    headers = {"Cache-Control": "no-store", "Content-Type": "application/json; charset=utf-8"}
                    def read(self, limit): self_test.assertEqual(8193, limit); return raw
                    def __enter__(self): return self
                    def __exit__(self, *args): actions.append("response-close")
                class Opener:
                    def open(self, request, timeout):
                        actions.append(("upstream", request.full_url, request.data, timeout))
                        return Response()
                self_test = self
                namespace = dict(http=SimpleNamespace(server=SimpleNamespace(BaseHTTPRequestHandler=object)),
                    urllib=spool_smoke.urllib, json=json, tenant=tenant, company=company, source=source,
                    api="http://127.0.0.1:8080", captured=captured,
                    committed=SimpleNamespace(set=lambda: actions.append("committed")),
                    release=SimpleNamespace(wait=lambda timeout: actions.append(("withheld", timeout))))
                exec(block, namespace)
                proxy = namespace["LostReply"]()
                proxy.path = "/internal/group-ingress/" + path
                proxy.headers = Message(); proxy.headers["Content-Length"] = "2"
                for name in ("Service", "Epoch", "Signed-At", "Nonce", "Signature"):
                    proxy.headers["X-AIOffice-Group-" + name] = "owned-inert-" + name
                proxy.rfile = io.BytesIO(b"{}")
                proxy.wfile = io.BytesIO()
                proxy.send_response = lambda code: actions.append(("status", code))
                proxy.send_header = lambda name, value: actions.append(("header", name, value))
                proxy.end_headers = lambda: actions.append("headers-end")
                with patch.object(spool_smoke.urllib.request, "build_opener", return_value=Opener()) as opener:
                    proxy.do_POST()
                self.assertEqual({}, opener.call_args.args[0].proxies)
                self.assertIsNone(opener.call_args.args[1].redirect_request(None, None, None, None, None, None))
                self.assertTrue(proxy.close_connection); self.assertNotIn("failed", captured)
                self.assertIn(("upstream", "http://127.0.0.1:8080" + proxy.path, b"{}", 12), actions)
                if path == "events":
                    self.assertEqual(b"", proxy.wfile.getvalue())
                    self.assertEqual(json.loads(raw), captured["ack"])
                    self.assertIn("committed", actions); self.assertIn(("withheld", 15), actions)
                    self.assertFalse(any(isinstance(action, tuple) and action[0] == "status" for action in actions))
                else:
                    self.assertEqual(raw, proxy.wfile.getvalue()); self.assertNotIn("ack", captured)
                    self.assertNotIn("committed", actions); self.assertEqual(1, captured[proxy.path])
                    self.assertIn(("header", "Content-Length", str(len(raw))), actions)
                    self.assertIn(("header", "Cache-Control", "no-store"), actions)
                    if path == "listener": self.assertEqual(json.loads(raw), captured["lease_ack"])

    def test_spool_proxy_refuses_unknown_path_or_oversized_metadata_before_upstream(self):
        tree = ast.parse(Path(spool_smoke.__file__).read_text(encoding="utf-8"))
        handler = next(node for node in ast.walk(tree) if isinstance(node, ast.ClassDef) and node.name == "LostReply")
        block = compile(ast.fix_missing_locations(ast.Module([handler], type_ignores=[])), "<inert-spool-proxy-refusal>", "exec")
        for path, length in (("enrollment?source=other", 2), ("enrollment", 8193), ("listener", 8193), ("events", 65537)):
            with self.subTest(path=path, length=length):
                captured = {}
                namespace = dict(http=SimpleNamespace(server=SimpleNamespace(BaseHTTPRequestHandler=object)),
                    urllib=spool_smoke.urllib, captured=captured,
                    committed=SimpleNamespace(set=lambda: None))
                exec(block, namespace)
                proxy = namespace["LostReply"](); proxy.path = "/internal/group-ingress/" + path
                proxy.headers = Message(); proxy.headers["Content-Length"] = str(length)
                with patch.object(spool_smoke.urllib.request, "build_opener", side_effect=AssertionError("unexpected upstream")) as opener:
                    proxy.do_POST()
                opener.assert_not_called()
                self.assertEqual({"failed": True, "failure_stage": "request-contract"}, captured)
                self.assertTrue(proxy.close_connection)

    def test_spool_latest_lease_oracle_rejects_foreign_account_owner_epoch_and_long_lease(self):
        tree = ast.parse(Path(spool_smoke.__file__).read_text(encoding="utf-8"))
        helper = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "load_lease")
        block = compile(ast.fix_missing_locations(ast.Module([helper], type_ignores=[])), "<inert-native-lease>", "exec")
        tenant, company, account, owner = (str(uuid.uuid4()) for _ in range(4))
        good = {"lease": {"account": {"tenantId": tenant, "companyId": company, "connectorAccountId": account},
            "ownerId": owner, "epoch": 1, "heartbeatAtUtc": "2026-10-10T00:00:00Z", "expiresAtUtc": "2026-10-10T00:00:30Z"}}
        class Root:
            def __truediv__(self, name): self_test.assertEqual("lease.json", name); return self
            def read_text(self, encoding): return json.dumps(value)
        self_test = self
        namespace = dict(root=Root(), json=json, datetime=spool_smoke.datetime,
            tenant=tenant, company=company, account=account)
        exec(block, namespace)
        value = good
        self.assertEqual(good, namespace["load_lease"](owner, 1))
        # The retained child file can hold the first Renew. Passing the last
        # observed actual response must select its later expiry unchanged.
        latest = {"lease": {**good["lease"], "heartbeatAtUtc": "2026-10-10T00:00:03Z", "expiresAtUtc": "2026-10-10T00:00:33Z"}}
        self.assertEqual(latest, namespace["load_lease"](owner, 1, latest))
        self.assertEqual(good, value)
        for field, changed in (("account", {**good["lease"]["account"], "connectorAccountId": str(uuid.uuid4())}),
                ("ownerId", str(uuid.uuid4())), ("epoch", 2), ("expiresAtUtc", "2026-10-10T00:00:31Z"),
                ("expiresAtUtc", "2026-10-10T00:00:00Z"), ("heartbeatAtUtc", "2026-10-10T00:00:00+01:00")):
            with self.subTest(field=field, changed=changed):
                value = {"lease": {**good["lease"], field: changed}}
                with self.assertRaises(AssertionError): namespace["load_lease"](owner, 1)

    def test_listener_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, {"CI": "false"}), (self.owned, {"GITHUB_ACTIONS": "false"}),
            (self.owned, {"RUNNER_TEMP": ""}), (self.root, {}), (self.owned / "nested", {}),
            (self.root / "retained", {})]
        def forbidden(*args, **kwargs): self.fail("Unowned listener proof touched resources")
        for directory, override in cases:
            with self.subTest(directory=str(directory), override=override), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Listener proof requires the owned disposable GitHub CI fixture\\.$"):
                    listener_smoke.verify(directory=directory, api="http://127.0.0.1:8080", tenant="invalid", company="invalid",
                        user="invalid", service="invalid", key=None, sql=forbidden, compose=[], environment={},
                        restart=forbidden, ready=forbidden, identity_index=forbidden, read_route=forbidden)
        with patch.dict(os.environ, self.environment, clear=True):
            for api in ("https://production.example", "http://localhost:8080", "http://127.0.0.1:8081"):
                with self.assertRaises(RuntimeError): listener_smoke.require_owned(self.owned, api)
            listener_smoke.require_owned(self.owned, "http://127.0.0.1:8080")

    def test_listener_signature_never_reuses_ingress_domain_and_binds_captured_bytes(self):
        service, nonce = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        body = b'{"command":{"operation":1}}'
        listener = listener_smoke.signing_bytes(service, 1, 1700000000, nonce, body)
        ingress = group_smoke.signing_bytes(service, 1, 1700000000, nonce, body)
        self.assertEqual(listener.split(b"\n")[0], b"aioffice-group-listener-v1")
        self.assertNotEqual(listener, ingress)
        for changed in ((service, 2, 1700000000, nonce, body), (service, 1, 1700000001, nonce, body),
                (service, 1, 1700000000, "cccccccc-cccc-cccc-cccc-cccccccccccc", body),
                (service, 1, 1700000000, nonce, body + b" ")):
            self.assertNotEqual(listener, listener_smoke.signing_bytes(*changed))

    def test_listener_observed_fence_cleanup_attempts_all_resources_and_preserves_original_failure(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "queued_revoke")
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        class InjectedFailure(Exception): pass
        for fault in ("create", "observation", "popen", "stdin_write", "stdin_close", "executor", "release", "drain", "shutdown", "restore", "drop"):
            with self.subTest(fault=fault):
                effects = []
                def effect(name):
                    effects.append(name)
                    if name == fault: raise InjectedFailure(name)
                class Input:
                    def write(self, query): effect("stdin_write")
                    def close(self): effect("stdin_close")
                class Process:
                    stdin = Input()
                    def communicate(self, timeout): effect("drain")
                    def kill(self): effect("kill")
                    def poll(self): return None
                def popen(*args, **kwargs): effect("popen"); return Process()
                class Executor:
                    def __init__(self, **kwargs): effect("executor")
                    def shutdown(self, **kwargs): effect("shutdown")
                def sql(query):
                    if "CREATE TABLE" in query: effect("create"); return ""
                    if "SET Released=1" in query: effect("release"); return ""
                    if query == "restore": effect("restore"); return ""
                    if "DROP TABLE" in query: effect("drop"); return ""
                    if "sys.dm_tran_locks" in query: effect("observation"); raise InjectedFailure("observation")
                    self.fail("Unexpected inert listener SQL")
                identifier = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
                namespace = {"sql": sql, "snapshot": lambda: ["AB" * 32] * 3, "tenant": identifier, "company": identifier, "account": identifier,
                    "uuid": SimpleNamespace(UUID=listener_smoke.uuid.UUID, uuid4=lambda: SimpleNamespace(hex="probe")),
                    "subprocess": SimpleNamespace(Popen=popen, PIPE=None), "ThreadPoolExecutor": Executor,
                    "time": SimpleNamespace(monotonic=lambda: 0), "compose": ["owned-compose"], "environment": {}}
                exec(compile(module, "inert_listener_cleanup", "exec"), namespace)
                with self.assertRaises(InjectedFailure) as failure: namespace["queued_revoke"]("revoke", "restore")
                self.assertEqual(fault if fault in ("create", "popen", "stdin_write", "stdin_close", "executor") else "observation", str(failure.exception))
                self.assertIn("release", effects); self.assertIn("restore", effects); self.assertIn("drop", effects)
                if fault not in ("create", "popen"): self.assertIn("drain", effects)
                if fault not in ("create", "popen", "stdin_write", "stdin_close", "executor"): self.assertIn("shutdown", effects)

    def test_listener_temporary_sql_restores_applied_setup_with_lost_reply_and_keeps_first_error(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "temporary_sql")
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        class InjectedFailure(Exception): pass
        for fault in ("setup", "body", "restore", "setup-and-restore"):
            with self.subTest(fault=fault):
                effects = []
                def sql(query):
                    effects.append(query + "-applied")
                    if query == "setup" and fault in ("setup", "setup-and-restore"): raise InjectedFailure("setup")
                    if query == "restore" and fault in ("restore", "setup-and-restore"): raise InjectedFailure("restore")
                def action():
                    effects.append("body")
                    if fault == "body": raise InjectedFailure("body")
                namespace = {"sql": sql}
                exec(compile(module, "inert_listener_temporary_sql", "exec"), namespace)
                with self.assertRaises(InjectedFailure) as failure: namespace["temporary_sql"]("setup", "restore", action)
                self.assertEqual("setup" if fault in ("setup", "setup-and-restore") else fault, str(failure.exception))
                self.assertIn("restore-applied", effects)
                self.assertEqual(fault not in ("setup", "setup-and-restore"), "body" in effects)

    def test_group_browser_diagnostics_retain_only_fixed_stage_or_bounded_http_status(self):
        prefix = "FAIL owned group Chromium inbox gate: "
        for stage in ("native-reader-grant-ui-refusal", "native-reader-grant-private-http-401", "native-reader-grant-catalog-http-503",
                "native-reader-grant-private-http-200"):
            self.assertEqual(stage, group_smoke.browser_failure_stage(prefix + stage))
        for stage in ("native-reader-grant-private-http-600", "native-reader-grant-private-http-99", "other-http-401",
                "native-reader-grant-private-http-401 PRIVATE_BODY", "PRIVATE_CREDENTIAL", "a" * 81, "native-reader-grant-ui-refusal\nPRIVATE_BODY"):
            self.assertIsNone(group_smoke.browser_failure_stage(prefix + stage))
        self.assertIsNone(group_smoke.browser_failure_stage("OTHER PRIVATE_BODY"))

    def test_listener_permission_oracle_accepts_redundant_deny_absence_and_requires_real_escalation(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "temporary_sql")
        def assigns(node, name):
            return isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id == name for target in node.targets)
        start = next(i for i, node in enumerate(verify.body) if assigns(node, "column_permission"))
        end = next(i for i in range(start, len(verify.body)) if isinstance(verify.body[i], ast.Assign)
            and isinstance(verify.body[i].targets[0], ast.Tuple))
        module = ast.fix_missing_locations(ast.Module(body=[helper, *verify.body[start:end]], type_ignores=[]))
        for baseline, fault in (("", None), ("D", None), ("G", "column"), ("", "table"),
                ("", "direct"), ("", "initial-effective"), ("", "ineffective-grant")):
            with self.subTest(baseline=baseline, fault=fault):
                effects = []; current = {"granted": False}
                def sql(query):
                    if query.startswith("GRANT UPDATE"):
                        effects.append("grant"); current["granted"] = True; return ""
                    if query.startswith("DENY UPDATE"):
                        effects.append("restore"); current["granted"] = False; return ""
                    if "HAS_PERMS_BY_NAME" in query:
                        return "1" if fault == "initial-effective" or current["granted"] and fault != "ineffective-grant" else "0"
                    if query.startswith("SELECT COUNT(*)"): return "1" if fault == "direct" else "0"
                    if "minor_id=0" in query: return "G" if fault == "table" else "D"
                    if query.startswith("SELECT state"): return "G" if current["granted"] else baseline
                    self.fail("Unexpected inert permission query")
                def no_effect(body):
                    self.assertTrue(current["granted"]); effects.append("refusal")
                original = {"wasAlreadyCommitted": False}
                namespace = {"sql": sql, "no_effect": no_effect, "command": lambda: b"owned-command", "body": b"owned-command",
                    "nonce": "owned-nonce", "original": original,
                    "signed_at": 1700000000, "renewal_count": 8,
                    "timing": lambda *args: self.assertFalse(current["granted"]),
                    "renew_and_replay": lambda: self.assertFalse(current["granted"]),
                    "renew_owned": lambda: self.assertFalse(current["granted"]),
                    "call": lambda *args, **kwargs: (200, {"wasAlreadyCommitted": True}), "receipt": lambda status, value, **kwargs: value}
                if fault is None:
                    exec(compile(module, "inert_listener_permissions", "exec"), namespace)
                    self.assertEqual(["grant", "refusal", "restore"], effects)
                else:
                    with self.assertRaises(AssertionError): exec(compile(module, "inert_listener_permissions", "exec"), namespace)
                    self.assertEqual(["grant", "restore"] if fault == "ineffective-grant" else [], effects)
                self.assertFalse(current["granted"])

    def test_listener_foreground_renewal_rejects_changed_history_extra_receipts_and_extended_duration(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "renew_owned")
        factory = ast.parse("def factory():\n    renewal_count = 0\n    return None\n").body[0]
        factory.body.insert(1, helper)
        factory.body[-1].value = ast.Name(id='renew_owned', ctx=ast.Load())
        module = ast.fix_missing_locations(ast.Module(body=[factory], type_ignores=[]))
        for fault in (None, "gap", "receipt", "extra-receipt", "no-receipt", "duration", "denial"):
            with self.subTest(fault=fault):
                histories = [["A" * 64, "B" * 64, "4"], ["A" * 64, "B" * 64, "5"]]
                if fault == "gap": histories[1][0] = "C" * 64
                if fault == "receipt": histories[1][1] = "C" * 64
                if fault == "extra-receipt": histories[1][2] = "6"
                if fault == "no-receipt": histories[1][2] = "4"
                effects = []
                def history(nonce): effects.append(('history', nonce)); return histories.pop(0)
                def call(body, **kwargs): effects.append(('call', kwargs['nonce'])); return (403 if fault == "denial" else 200), {}
                def receipt(status, value, **kwargs):
                    self.assertEqual(200, status)
                    self.assertEqual({'process': 'owned-owner', 'epoch': 1, 'coverage': False}, kwargs)
                    return {'lease': {'heartbeatAtUtc': '2026-10-10T00:00:00+00:00',
                        'expiresAtUtc': '2026-10-10T00:00:' + ('31' if fault == "duration" else '30') + '+00:00'}}
                namespace = {'owner': 'owned-owner', 'command': lambda *args: b'owned-renew', 'uuid': uuid,
                    'time': SimpleNamespace(time=lambda: 1700000000), 'immutable_history': history,
                    'call': call, 'receipt': receipt, 'timestamp': listener_smoke.datetime.fromisoformat}
                exec(compile(module, 'actual-owned-renewal', 'exec'), namespace)
                action = namespace['factory']()
                if fault is None:
                    captured = action()
                    self.assertEqual(b'owned-renew', captured[0])
                    self.assertEqual([('history', captured[1]), ('call', captured[1]), ('history', captured[1])], effects)
                else:
                    with self.assertRaises(AssertionError): action()
                    self.assertEqual(1, sum(kind == 'call' for kind, _ in effects))

    def test_listener_renewal_history_refuses_malformed_metadata_without_echo(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding='utf-8'))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'verify')
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == 'immutable_history')
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        valid = 'A' * 64 + '|' + 'B' * 64 + '|5'
        for value in (valid, 'PRIVATE_CREDENTIAL', valid + '\nPRIVATE_CREDENTIAL', valid.replace('|5', '|-1'), valid.replace('A', 'g')):
            namespace = {'sql': lambda query: value, 'account_scope': 'owned-scope', 're': re}
            exec(compile(module, 'actual-renewal-history', 'exec'), namespace)
            if value == valid:
                self.assertEqual(['A' * 64, 'B' * 64, '5'], namespace['immutable_history']('owned-nonce'))
            else:
                with self.assertRaisesRegex(AssertionError, '^Invalid owned renewal history metadata$'):
                    namespace['immutable_history']('owned-nonce')

    def test_listener_restored_positive_replays_captured_renewal_and_requires_unchanged_graph(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding='utf-8'))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'verify')
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == 'renew_and_replay')
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        for fault in (None, 'ack', 'graph'):
            snapshots = iter((['original-graph'], ['changed-graph' if fault == 'graph' else 'original-graph']))
            def call(body, **kwargs):
                self.assertEqual(b'captured-renew', body)
                self.assertEqual({'nonce': 'captured-nonce', 'signed_at': 1700000000}, kwargs)
                return 200, {'wasAlreadyCommitted': True, 'changed': fault != 'ack'}
            def receipt(status, value, **kwargs):
                self.assertEqual({'coverage': False, 'already': True}, kwargs)
                return value
            namespace = {'renew_owned': lambda: (b'captured-renew', 'captured-nonce', 1700000000,
                {'wasAlreadyCommitted': False, 'changed': True}), 'snapshot': lambda: next(snapshots), 'call': call, 'receipt': receipt}
            exec(compile(module, 'actual-renewal-replay', 'exec'), namespace)
            if fault is None: namespace['renew_and_replay']()
            else:
                with self.assertRaises(AssertionError): namespace['renew_and_replay']()

    def test_listener_timing_uses_one_server_snapshot_and_only_fixed_flags(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding='utf-8'))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'verify')
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == 'timing')
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        for result in ('1|1|1', '0|1|1', '1|0|1', '1|1|0', 'PRIVATE_CREDENTIAL', '1|1|1\nPRIVATE_CREDENTIAL'):
            queries, messages = [], []
            def sql(query): queries.append(query); return result
            namespace = {'sql': sql, 're': re, 'print': messages.append,
                **{name: 'owned-id' for name in ('tenant', 'company', 'account', 'service')}}
            exec(compile(module, 'actual-owned-clock', 'exec'), namespace)
            if re.fullmatch(r'[01]\|[01]\|[01]', result):
                self.assertEqual(result, namespace['timing']('owned-nonce', 1700000000))
                self.assertEqual(['INFO owned listener timing expired-receipt/live-lease/fresh-signature=' + result], messages)
            else:
                with self.assertRaisesRegex(AssertionError, '^Invalid owned listener timing metadata$'):
                    namespace['timing']('owned-nonce', 1700000000)
                self.assertEqual([], messages)
            self.assertEqual(1, len(queries))
            self.assertEqual(1, queries[0].count('SYSUTCDATETIME()'))
            self.assertIn('DATEDIFF_BIG(millisecond', queries[0])
            self.assertIn('l.OwnerId=r.OwnerId AND l.Epoch=r.ListenerEpoch', queries[0])

    def test_final_group_read_race_cleanup_attempts_all_owned_resources_and_preserves_first_failure(self):
        # Execute only the shipping helper's orchestration with inert dependencies.
        # No files, SQL, processes, threads or HTTP are created by these controls.
        tree = ast.parse(Path(group_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "read_release_range")
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))

        class InjectedFailure(Exception): pass
        for fault in ("observation", "popen", "stdin_write", "stdin_close", "executor", "release", "drain", "shutdown", "restore", "drop"):
            with self.subTest(fault=fault):
                effects = []
                def effect(name):
                    effects.append(name)
                    if name == fault: raise InjectedFailure(name)
                class Input:
                    def write(self, query): effect("stdin_write")
                    def close(self): effect("stdin_close")
                class Process:
                    stdin = Input()
                    returncode = 0
                    def communicate(self, timeout): effect("drain")
                    def kill(self): effect("kill")
                    def poll(self): return None
                def popen(*args, **kwargs): effect("popen"); return Process()
                class Executor:
                    def __init__(self, **kwargs): effect("executor")
                    def shutdown(self, **kwargs): effect("shutdown")
                def sql(query):
                    if "SELECT CommittedSequence" in query: return "1"
                    if "HASHBYTES" in query: return "AB" * 32
                    if "CREATE TABLE" in query: effect("create"); return ""
                    if "SET Released=1" in query: effect("release"); return ""
                    if "UPDATE aioffice.GroupReaderGrants" in query: effect("restore"); return ""
                    if "DROP TABLE" in query: effect("drop"); return ""
                    if "sys.dm_tran_locks" in query: effect("observation"); raise InjectedFailure("observation")
                    self.fail("Unexpected inert fixture SQL")
                namespace = {"re": re, "sql": sql, "counts": lambda: [1, 1, 1, 1], "event": lambda **kwargs: {},
                    "uuid": SimpleNamespace(uuid4=lambda: SimpleNamespace(hex="probe")),
                    "subprocess": SimpleNamespace(Popen=popen, PIPE=None), "ThreadPoolExecutor": Executor,
                    "time": SimpleNamespace(monotonic=lambda: 0), "compose": ["owned-compose"], "environment": {},
                    "scope": "TenantId='owned' AND CompanyId='owned' AND BindingId='owned'", "owner": "owned"}
                exec(compile(module, "owned_inert_read_race", "exec"), namespace)
                with self.assertRaises(InjectedFailure) as failure:
                    namespace["read_release_range"]({"event": {"messageId": "owned"}}, {})
                self.assertEqual(fault if fault in ("popen", "stdin_write", "stdin_close", "executor") else "observation", str(failure.exception))
                self.assertIn("release", effects); self.assertIn("restore", effects); self.assertIn("drop", effects)
                if fault != "popen": self.assertIn("drain", effects)
                if fault not in ("popen", "stdin_write", "stdin_close", "executor"): self.assertIn("shutdown", effects)

    def test_exact_owned_ci_fixture(self):
        with patch.dict(os.environ, self.environment, clear=True):
            smoke.require_owned_ci_fixture(self.owned)

    def test_unowned_execution_flags(self):
        for override in ({"CI": "false"}, {"GITHUB_ACTIONS": "false"}, {"RUNNER_TEMP": ""}):
            with self.subTest(override=override), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Adversarial stack proof requires the owned disposable GitHub CI fixture\\.$"):
                    smoke.require_owned_ci_fixture(self.owned)

    def test_retained_nested_or_unrelated_directory(self):
        for directory in (self.root, self.root / "retained", self.owned / "nested", self.root.parent / "aioffice-local"):
            with self.subTest(directory=directory.name), patch.dict(os.environ, self.environment, clear=True):
                with self.assertRaises(RuntimeError):
                    smoke.require_owned_ci_fixture(directory.resolve())

    def test_administrator_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, {"CI": "false"}), (self.owned, {"GITHUB_ACTIONS": "false"}),
                 (self.owned, {"RUNNER_TEMP": ""}), (self.root, {}), (self.root / "retained", {}),
                 (self.owned / "nested", {}), (self.root.parent / "aioffice-local", {})]
        for directory, override in cases:
            with self.subTest(directory=str(directory), override=override), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaises(AssertionError):
                    administrator_smoke.verify(directory=directory, manifest=None, compose=None, environment=None,
                        http=None, sql=None, runtime_statement=None, identity_admin=None, identity=None,
                        api="http://127.0.0.1:8080", web="http://127.0.0.1:3000", auth=None)

    def test_history_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080"),
            (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080"),
            (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080"),
            (self.root, {}, "http://127.0.0.1:8080"), (self.owned / "nested", {}, "http://127.0.0.1:8080"),
            (self.owned, {}, "https://customer.example.invalid")]
        for directory, override, api in cases:
            with self.subTest(directory=str(directory), override=override, api=api), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Archive proof requires the owned disposable GitHub CI fixture\\.$"):
                    history_smoke.verify(directory=directory, manifest=None, compose=None, environment=None,
                        http=None, sql=None, identity_admin=None, identity=None, api=api, auth=None, retained_task=None)

    def test_archive_large_sql_uses_stdin_without_query_in_argv_or_diagnostics(self):
        query = "SELECT N'PRIVATE_" + "x" * 200000 + "';"
        with patch.object(history_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="PASS\n", stderr="")) as command:
            self.assertEqual("PASS", history_smoke._owned_sql_stdin(["docker", "compose"], {}, query))
            args, kwargs = command.call_args
            self.assertTrue(all("PRIVATE_" not in item for item in args[0]))
            self.assertEqual("SET NOCOUNT ON; " + query, kwargs["input"])
            self.assertIn("/dev/stdin", args[0][-1])
        with patch.object(history_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=1, stdout="Msg 207, Level 16 PRIVATE_DIAGNOSTIC", stderr="PRIVATE_TOKEN")):
            with self.assertRaisesRegex(RuntimeError, r"^Owned archive SQL fixture command failed \(SQL message 207\)\.$"):
                history_smoke._owned_sql_stdin(["docker", "compose"], {}, query)

    def test_submission_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.root, {}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.owned / "nested", {}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.owned, {}, "https://customer.example.invalid", "http://127.0.0.1:8081"),
            (self.owned, {}, "http://127.0.0.1:8080", "https://customer.example.invalid")]
        for directory, override, api, identity in cases:
            with self.subTest(directory=str(directory), override=override, api=api, identity=identity), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Submission proof requires the owned disposable GitHub CI fixture\\.$"):
                    submission_smoke.verify(directory=directory, manifest=None, compose=None, environment=None,
                        http=None, sql=None, runtime_statement=None, identity=identity, api=api, auth=None)

    def test_submission_fingerprint_independent_python_vectors_and_scalar_rejection(self):
        source = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        for question, expected in [("Tồn kho 😀 �", "6354CD8F9D07F489B9F1B213CE89B4FE5EF4016DFC44329AE740B0C39BDE0656"),
            ("ồ", "653C057F6AFF5A8FF5FEAFA8B8CFAABAE942E33F0605BC03D14876C53B223DE5"),
            ("o\u0302\u0300", "7176B4A97A12F1F815F8133B9E33C7D021885765A6A5D560976A42104F0ED186")]:
            self.assertEqual(expected, submission_smoke.input_fingerprint(source, question))
        with self.assertRaises(UnicodeEncodeError):
            submission_smoke.input_fingerprint(source, "bad\ud800")

    def test_submission_first_commit_oracle_rejects_every_extra_effect_and_altered_credit(self):
        before = [10, 20, 30, 40, 50, 60, 0]
        after = [11, 21, 33, 41, 51, 61, 0]
        credit = "AB" * 32
        submission_smoke.require_single_execution_delta(before, after, credit, credit)
        for index in range(7):
            with self.subTest(extra_effect=index):
                extra = after.copy(); extra[index] += 1
                with self.assertRaises(AssertionError):
                    submission_smoke.require_single_execution_delta(before, extra, credit, credit)
        with self.assertRaises(AssertionError):
            submission_smoke.require_single_execution_delta(before, after, credit, "CD" * 32)
        with self.assertRaises(AssertionError):
            submission_smoke.require_single_execution_delta(before[:-1], after[:-1], credit, credit)

    def test_group_ingress_refuses_unowned_before_private_configuration_or_processes(self):
        cases = [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080"),
            (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080"),
            (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080"),
            (self.root, {}, "http://127.0.0.1:8080"), (self.owned / "nested", {}, "http://127.0.0.1:8080"),
            (self.owned, {}, "https://customer.example.invalid")]
        for directory, override, api in cases:
            with self.subTest(directory=str(directory), override=override, api=api), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Group ingress proof requires the owned disposable GitHub CI fixture\\.$"):
                    group_smoke.verify(directory=directory, manifest=None, compose=None, environment=None, api=api)

    def test_group_index_matches_accepted_shared_contract_golden_and_preserves_original_scalars(self):
        self.assertEqual("E6A0749FB48B2CC0E601E3412D462580886A13C3E34971A794B21C63C5E68599",
            group_smoke.identity_index("synthetic", "account ", " group😀 "))
        self.assertNotEqual(group_smoke.identity_index("synthetic", "account", "ồ"), group_smoke.identity_index("synthetic", "account", "o\u0302\u0300"))
        with self.assertRaises(UnicodeEncodeError): group_smoke.identity_index("synthetic", "account", "\ud800")

    def test_group_signing_has_fixed_domain_and_complete_original_body_fingerprint(self):
        service = "11111111-1111-4111-8111-111111111111"
        nonce = "22222222-2222-4222-8222-222222222222"
        expected = ("aioffice-group-ingest-v1\n" + service + "\n1\n1791580000\n" + nonce +
            "\nE3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855").encode("ascii")
        self.assertEqual(expected, group_smoke.signing_bytes(service, 1, 1791580000, nonce, b""))
        self.assertNotEqual(expected, group_smoke.signing_bytes(service, 1, 1791580000, nonce, b" "))

    def test_group_cursor_oracle_refuses_skipped_future_and_duplicate_commits(self):
        group_smoke.require_contiguous_cursor([1, 2], 2)
        for sequences, cursor in [([1, 3], 2), ([1, 1], 2), ([2, 3], 2), ([1], 2), ([1, 2, 3], 2), ([], 0)]:
            with self.subTest(sequences=sequences, cursor=cursor), self.assertRaises(AssertionError):
                group_smoke.require_contiguous_cursor(sequences, cursor)

    def test_group_private_override_is_removed_even_when_restore_process_or_readiness_fails(self):
        class DisabledResponse:
            status = 404
            headers = {"Cache-Control": "no-store"}
            def read(self): return b""
            def __enter__(self): return self
            def __exit__(self, *args): pass
        for restore_failure in (True, False):
            with self.subTest(restore_failure=restore_failure), tempfile.TemporaryDirectory(prefix="aioffice-group-guard-") as root:
                directory = Path(root) / "aioffice-local"; directory.mkdir()
                marker = directory / "retained-unowned-marker"; marker.write_text("retained", encoding="utf-8")
                environment = {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": root}
                manifest = {"AIOFFICE_TENANT_ID": "11111111-1111-4111-8111-111111111111", "AIOFFICE_COMPANY_ID": "22222222-2222-4222-8222-222222222222", "AIOFFICE_USER_ID": "33333333-3333-4333-8333-333333333333"}
                auth = {"Authorization": "Bearer owned-fixture", "X-AIOffice-Company-Id": manifest["AIOFFICE_COMPANY_ID"]}
                with patch.dict(os.environ, environment, clear=True), patch.object(group_smoke.urllib.request, "urlopen", return_value=DisabledResponse()), \
                    patch.object(group_smoke, "owned_sql", side_effect=RuntimeError("Owned SQL admission failure.")), \
                    patch.object(group_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=1 if restore_failure else 0, stdout="", stderr="")), \
                    patch.object(group_smoke.time, "monotonic", side_effect=[0, 61]):
                    expected = "Owned group API recreation failed." if restore_failure else "Owned group API readiness deadline exceeded."
                    with self.assertRaisesRegex(RuntimeError, "^" + re.escape(expected) + "$"):
                        group_smoke.verify(directory=directory, manifest=manifest, compose=["docker", "compose"], environment=environment, api="http://127.0.0.1:8080", auth=auth)
                self.assertFalse((directory / "group-ingress-owned.override.json").exists())
                self.assertEqual("retained", marker.read_text(encoding="utf-8"))

    def test_group_history_transport_refuses_default_sqlcmd_max_type_truncation(self):
        original = '["' + 'x'*400 + '"]'
        expected_bytes = len(original.encode('utf-16-le'))
        self.assertEqual(['x'*400], group_smoke.decode_bounded_history(original, expected_bytes))
        for text, size in [(original[:256], expected_bytes), (original[:-1], expected_bytes), (original, 8002), ('', 0)]:
            with self.subTest(length=len(text), size=size), self.assertRaises(AssertionError):
                group_smoke.decode_bounded_history(text, size)

    def test_group_sql_private_bytes_stay_in_stdin_and_diagnostics_remain_fixed(self):
        private = "SELECT N'PRIVATE_GROUP_FIXTURE';"
        with patch.object(group_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="PASS\n", stderr="")) as command:
            self.assertEqual("PASS", group_smoke.owned_sql(["docker", "compose"], {}, private))
            args, kwargs = command.call_args
            self.assertTrue(all("PRIVATE_GROUP" not in item for item in args[0]))
            self.assertEqual("SET NOCOUNT ON; USE AIOfficeLocal; " + private, kwargs["input"])
        with patch.object(group_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=1, stdout="Msg 207, Level 16 PRIVATE_GROUP", stderr="PRIVATE_TOKEN")):
            with self.assertRaisesRegex(RuntimeError, r"^Owned group SQL fixture command failed \(SQL message 207\)\.$"):
                group_smoke.owned_sql(["docker", "compose"], {}, private)

    def test_submission_original_event_transport_refuses_silent_sqlcmd_truncation(self):
        original = "7B00" * 300
        self.assertEqual(original, submission_smoke.require_stored_event_hex(original, 600))
        for value, size in [(original[:256], 600), (original[:-1], 600), ("NULL", 600), ("gg" * 600, 600), ("AA" * 4001, 4001)]:
            with self.subTest(length=len(value), expected_bytes=size), self.assertRaises(AssertionError):
                submission_smoke.require_stored_event_hex(value, size)


class HostBrainFixtureMetadataTests(unittest.TestCase):
    def fixture(self):
        cipher = bytes([1]) + bytes(29)
        message = '33333333-3333-4333-8333-333333333333'
        return {'operationId': '11111111-1111-4111-8111-111111111111',
            'batchId': '22222222-2222-4222-8222-222222222222', 'messageId': message,
            'messageRevision': 1, 'claimEpoch': 9, 'sourceVersion': 1, 'deletionGeneration': 0,
            'credentialEpoch': 1, 'grantVersion': 1, 'accountVersion': 1,
            'createdAtUtc': '2026-10-11T01:00:00.0000001+00:00',
            'sourceSetHash': hashlib.sha256((message + '/1').encode('ascii')).hexdigest().upper(),
            'notes': [{'requestId': f'{i:08d}-4444-4444-8444-444444444444', 'ordinal': i, 'kind': 5 if i == 3 else 4,
                'envelope': cipher.hex().upper(), 'envelopeHash': hashlib.sha256(cipher).hexdigest().upper()} for i in range(1, 4)]}

    def refused(self, value):
        with self.assertRaises((AssertionError, ValueError, TypeError, UnicodeError)):
            reference_smoke.validated_host_brain_fixture(json.dumps(value))

    def test_closed_metadata_positive_does_not_mutate_or_qualify_fake_cipher(self):
        value = self.fixture(); original = copy.deepcopy(value)
        self.assertEqual(value, reference_smoke.validated_host_brain_fixture(json.dumps(value)))
        self.assertEqual(original, value)

    def test_refuses_unknown_missing_and_duplicate_decoded_names(self):
        for field in self.fixture():
            value = self.fixture(); del value[field]
            with self.subTest(missing=field): self.refused(value)
        value = self.fixture(); value['sql'] = 'PRIVATE'; self.refused(value)
        value = self.fixture(); value['notes'][0]['key'] = 'PRIVATE'; self.refused(value)
        raw = json.dumps(self.fixture())
        for raw in ('{"operationId":"duplicate",' + raw[1:], raw.replace('"ordinal": 1', '"ordinal": 1, "ordi\\u006eal": 1')):
            with self.assertRaises(AssertionError): reference_smoke.validated_host_brain_fixture(raw)

    def test_refuses_noncanonical_zero_and_injection_identities(self):
        for field in ('operationId', 'batchId', 'messageId'):
            for wrong in ('00000000-0000-0000-0000-000000000000', '{AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA}',
                    'AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA', 'PRIVATE\'; SQL', True):
                value = self.fixture(); value[field] = wrong
                with self.subTest(field=field, wrong=wrong): self.refused(value)
        value = self.fixture(); value['notes'][1]['requestId'] = value['notes'][0]['requestId']; self.refused(value)
        value = self.fixture(); value['notes'][0]['requestId'] = '00000000-0000-0000-0000-000000000000'; self.refused(value)

    def test_refuses_boolean_float_unbounded_epochs_versions_and_wrong_claim(self):
        for field in ('messageRevision', 'claimEpoch', 'sourceVersion', 'deletionGeneration', 'credentialEpoch', 'grantVersion', 'accountVersion'):
            for wrong in (True, 1.0, -1, 9223372036854775808, '1'):
                value = self.fixture(); value[field] = wrong
                with self.subTest(field=field, wrong=wrong): self.refused(value)
            if field != 'deletionGeneration':
                value = self.fixture(); value[field] = 0; self.refused(value)
        value = self.fixture(); value['claimEpoch'] = 8; self.refused(value)

    def test_refuses_cardinality_order_and_reason_kind_mismatch(self):
        for count in (0, 1, 2, 4):
            value = self.fixture(); value['notes'] = (value['notes'] * 2)[:count]; self.refused(value)
        for index in range(3):
            for field, wrong in (('ordinal', True), ('ordinal', index+2), ('kind', True), ('kind', 1), ('kind', 4 if index == 2 else 5)):
                value = self.fixture(); value['notes'][index][field] = wrong
                with self.subTest(index=index, field=field, wrong=wrong): self.refused(value)

    def test_refuses_malformed_or_altered_cipher_and_source_hash(self):
        for wrong in ('', '01', '01' + 'AA' * 1024, '01' + 'aa' * 29, '01' + 'AA' * 29 + 'A', '00' + '00' * 29, 'GG' * 30):
            value = self.fixture(); value['notes'][0]['envelope'] = wrong
            with self.subTest(envelope_length=len(wrong)): self.refused(value)
        for field in ('sourceSetHash',):
            value = self.fixture(); value[field] = 'B' * 64; self.refused(value)
        value = self.fixture(); value['notes'][0]['envelopeHash'] = 'B' * 64; self.refused(value)
        value = self.fixture(); value['messageRevision'] = 2; self.refused(value)

    def test_refuses_non_UTC_calendar_scalar_and_UTF8_byte_overflow(self):
        for wrong in ('2026-10-11T01:00:00.0000001Z', '2026-10-11T01:00:00.0000001+07:00',
                '2026-02-30T01:00:00.0000001+00:00', True):
            value = self.fixture(); value['createdAtUtc'] = wrong; self.refused(value)
        for raw in ('', '\ud800', ' ' * 8193, json.dumps(self.fixture()) + ' ' * 8192):
            with self.assertRaises((AssertionError, ValueError, UnicodeError)):
                reference_smoke.validated_host_brain_fixture(raw)


class NoteCapacityOracleTests(unittest.TestCase):
    def run_probe(self, fault=None, changed=False):
        statements, comparisons = [], []
        def sql(statement):
            statements.append(statement)
            self.assertIn("SET XACT_ABORT ON; BEGIN TRY BEGIN TRANSACTION;", statement)
            self.assertIn("ROLLBACK; SELECT N'owned-capacity-accepted';", statement)
            self.assertIn("IF @@TRANCOUNT>0 ROLLBACK;", statement)
            self.assertIn("IF @@ROWCOUNT<>1 THROW 51901", statement)
            value = (20, 21, 40, 0, 41)[(len(statements) - 1) % 5]
            if fault == "unrelated-547": return "wrong-constraint-denied"
            if fault == "missing-row": return "owned-capacity-source-missing"
            if fault == "accept-out-of-bounds": return "owned-capacity-accepted"
            if fault == "deny-valid": return "owned-capacity-ck-denied"
            return "owned-capacity-accepted" if value in (20, 21, 40) else "owned-capacity-ck-denied"
        def compare():
            comparisons.append(1)
            if changed: raise AssertionError("changed retained graph")
        with tempfile.TemporaryDirectory(prefix="aioffice-capacity-oracle-") as root:
            directory = Path(root) / "aioffice-local"; directory.mkdir()
            with patch.dict(os.environ, {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": root}, clear=True):
                reference_smoke.verify_owned_note_capacity(directory=directory, api="http://127.0.0.1:8080",
                    tenant=str(uuid.uuid4()), company=str(uuid.uuid4()), source=str(uuid.uuid4()),
                    host_operation=str(uuid.uuid4()), host_request=str(uuid.uuid4()), sql=sql, assert_unchanged=compare)
        return statements, comparisons

    def test_shipping_probe_covers_twenty_boundary_cases_specific_constraints_and_retained_graph(self):
        statements, comparisons = self.run_probe()
        self.assertEqual(20, len(statements)); self.assertEqual(20, len(comparisons))
        for offset, constraint in enumerate(("CK_GroupWorkCommitReceipts_Counts", "CK_GroupCustomerRequests_Values",
                "CK_GroupNotesCommittedOutbox_Values", "CK_GroupNotesCommittedItems_Values")):
            for statement in statements[offset * 5:(offset + 1) * 5]:
                self.assertIn(f"ERROR_NUMBER()=547 AND CHARINDEX(N'{constraint}',ERROR_MESSAGE())>0", statement)
                self.assertIn("ELSE THROW;", statement)

    def test_shipping_probe_refuses_wrong_error_missing_row_acceptance_and_graph_mutation(self):
        for fault in ("unrelated-547", "missing-row", "accept-out-of-bounds", "deny-valid"):
            with self.subTest(fault=fault), self.assertRaises(AssertionError): self.run_probe(fault=fault)
        with self.assertRaisesRegex(AssertionError, "changed retained graph"): self.run_probe(changed=True)

    def test_unowned_and_noncanonical_scope_denied_before_callbacks(self):
        calls = []
        arguments = dict(directory=Path("unowned"), api="http://127.0.0.1:8080", tenant=str(uuid.uuid4()),
            company=str(uuid.uuid4()), source=str(uuid.uuid4()), host_operation=str(uuid.uuid4()), host_request=str(uuid.uuid4()),
            sql=lambda _: calls.append("sql"), assert_unchanged=lambda: calls.append("graph"))
        with patch.dict(os.environ, {}, clear=True), self.assertRaises(RuntimeError):
            reference_smoke.verify_owned_note_capacity(**arguments)
        with tempfile.TemporaryDirectory(prefix="aioffice-capacity-oracle-") as root:
            arguments["directory"] = Path(root) / "aioffice-local"
            arguments["tenant"] = "noncanonical' SQL"
            with patch.dict(os.environ, {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": root}, clear=True), self.assertRaises((AssertionError, ValueError)):
                reference_smoke.verify_owned_note_capacity(**arguments)
        self.assertEqual([], calls)


class EffectPreparationOracleTests(unittest.TestCase):
    def run_actual_prefix(self, *, mutate=None, fault=None):
        # Execute the shipping prefix, stopping before protected brain inserts.
        # Inert mutations test its oracle; they never qualify SQL behavior.
        function = next(node for node in ast.parse(Path(reference_spec.origin).read_text(encoding="utf-8")).body
            if isinstance(node, ast.FunctionDef) and node.name == "verify_fixed_effects")
        prefix = copy.deepcopy(function)
        boundary = next(index for index, node in enumerate(prefix.body) if isinstance(node, ast.Assign)
            and any(isinstance(target, ast.Name) and target.id == "allocated_graph" for target in node.targets))
        prefix.body = prefix.body[:boundary + 1] + [ast.Return(value=ast.Name(id="allocated_graph", ctx=ast.Load()))]
        module = ast.fix_missing_locations(ast.Module(body=[prefix], type_ignores=[]))
        state = {"launches": 0, "before_digests": [], "retained_checks": 0}
        def sql(statement):
            if "HASHBYTES" in statement:
                table = re.search(r"FROM aioffice\.(\w+)", statement)[1]
                if not state["launches"]: state["before_digests"].append(table)
                return ("B" if state["launches"] and mutate == table else "A") * 64
            if "GroupAccountCoverageGaps" in statement: return "0|0"
            if "CONCAT" in statement: return "2|4|1"
            if not state["launches"]:
                if "GroupSourceStates" in statement:
                    # Actual ingress scheduling anchors can be reanchored
                    # after saving immutable revisions; equality is false.
                    if "FirstPendingAtUtc=(" in statement or "LastPendingAtUtc=(" in statement: return "0"
                    return "0" if fault == "pending-before" else "1"
                table = re.search(r"FROM aioffice\.(\w+)", statement)[1]
                return "1" if fault == "preexisting-" + table else "0"
            if "GroupSourceStates" in statement: return "0" if fault == "pending-after" else "1"
            if "EXCEPT" in statement: return "1" if fault == "revision-metadata" else "0"
            if "GroupIngressInbox i JOIN" in statement: return "1" if fault == "inbox-metadata" else "2"
            if "GroupBatchAllocations" in statement: return "2" if fault == "allocation" else "1"
            if "GroupBatchAllocatedRevisions" in statement: return "3" if fault == "allocated-rows" else "2"
            if "GroupBatchClaimStates" in statement: return "2" if fault == "claim-state" else "1"
            if "GroupBatchClaimReceipts" in statement: return "3" if fault == "claim-receipts" else "4"
            self.fail("Unexpected inert preparation SQL")
        def launch(*args, **kwargs):
            state["launches"] += 1
            return SimpleNamespace(returncode=0, stdout="bounded-private-fixture")
        def retained(): state["retained_checks"] += 1
        namespace = dict(reference_smoke.__dict__)
        namespace["subprocess"] = SimpleNamespace(run=launch)
        namespace["validated_brain_fixture"] = lambda _: {"batchId": str(uuid.uuid4()), "credentialEpoch": 1, "grantVersion": 1}
        exec(compile(module, str(reference_spec.origin), "exec"), namespace)
        with tempfile.TemporaryDirectory(prefix="aioffice-effect-oracle-") as root:
            directory = Path(root) / "aioffice-local"; directory.mkdir()
            with patch.dict(os.environ, {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": root}, clear=True):
                graph = namespace["verify_fixed_effects"](directory=directory, api="http://127.0.0.1:8080",
                    tenant=str(uuid.uuid4()), company=str(uuid.uuid4()), service=str(uuid.uuid4()), source=str(uuid.uuid4()),
                    events=[str(uuid.uuid4()), str(uuid.uuid4())], source_key=base64.b64encode(bytes(32)).decode(), sql=sql,
                    command=["inert"], child_environment={}, assert_retained=retained)
        return graph, state

    def test_actual_prefix_freezes_before_child_and_allows_exact_documented_additions(self):
        graph, state = self.run_actual_prefix()
        self.assertEqual(["A" * 64] * 9, graph)
        self.assertEqual(["GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts", "GroupCoverageGaps",
            "GroupIngressOutbox", "GroupSourceStates", "GroupSourceStates", "GroupSourceStates", "GroupSourceStates"], state["before_digests"])
        self.assertEqual(1, state["launches"])
        self.assertEqual(1, state["retained_checks"])

    def test_actual_prefix_refuses_each_immutable_source_mutation_during_child(self):
        for table in ("GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts", "GroupCoverageGaps",
                "GroupIngressOutbox", "GroupSourceStates"):
            with self.subTest(table=table), self.assertRaisesRegex(AssertionError, "changed immutable source bytes"):
                self.run_actual_prefix(mutate=table)

    def test_actual_prefix_refuses_preexisting_effects_and_wrong_pending_state(self):
        for fault in ("pending-before", "preexisting-GroupIngressInbox", "preexisting-GroupBatchAllocations",
                "preexisting-GroupBatchAllocatedRevisions", "preexisting-GroupBatchClaimStates", "preexisting-GroupBatchClaimReceipts"):
            with self.subTest(fault=fault), self.assertRaises(AssertionError): self.run_actual_prefix(fault=fault)

    def test_actual_prefix_refuses_inexact_source_inbox_allocation_and_claim_additions(self):
        for fault in ("pending-after", "revision-metadata", "inbox-metadata", "allocation", "allocated-rows", "claim-state", "claim-receipts"):
            with self.subTest(fault=fault), self.assertRaises(AssertionError): self.run_actual_prefix(fault=fault)


class ReferenceChildClosureTests(unittest.TestCase):
    class Child:
        def __init__(self, poll_error=None, drain_errors=(), input_error=None, completed=False):
            self.poll_error = poll_error
            self.drain_errors = list(drain_errors)
            self.drains = []
            self.completed = completed
            self.stdin = SimpleNamespace(close=lambda: self.close_input(input_error))
            self.input_closed = 0

        def close_input(self, error):
            self.input_closed += 1
            if error is not None: raise error

        def poll(self):
            if self.poll_error is not None: raise self.poll_error
            return 0 if self.completed else None

        def communicate(self, timeout):
            self.drains.append(timeout)
            if self.drain_errors:
                error = self.drain_errors.pop(0)
                if error is not None: raise error
            return ("", "")

    def test_poll_failure_still_attempts_owned_kill_and_drain_preserving_original(self):
        original = RuntimeError("owned-poll-fault")
        child = self.Child(poll_error=original)
        kills = []
        with self.assertRaises(RuntimeError) as observed:
            reference_smoke.close_owned_reference_child(child, lambda: kills.append("inspected-kill"))
        self.assertIs(original, observed.exception)
        self.assertEqual(["inspected-kill"], kills)
        self.assertEqual([15], child.drains)

    def test_kill_failure_cannot_skip_drain(self):
        original = RuntimeError("owned-kill-fault")
        child = self.Child()
        def kill(): raise original
        with self.assertRaises(RuntimeError) as observed:
            reference_smoke.close_owned_reference_child(child, kill)
        self.assertIs(original, observed.exception)
        self.assertEqual([15], child.drains)

    def test_drain_timeout_reinspects_kill_and_always_performs_final_bounded_drain(self):
        original = TimeoutError("owned-first-drain")
        child = self.Child(drain_errors=[original, None])
        kills = []
        with self.assertRaises(TimeoutError) as observed:
            reference_smoke.close_owned_reference_child(child, lambda: kills.append("inspected-kill"))
        self.assertIs(original, observed.exception)
        self.assertEqual(["inspected-kill", "inspected-kill"], kills)
        self.assertEqual([15, 5], child.drains)

    def test_every_failure_still_attempts_secondary_kill_and_final_drain(self):
        original = RuntimeError("owned-input-close")
        child = self.Child(poll_error=RuntimeError("poll"), drain_errors=[TimeoutError("first"), TimeoutError("final")], input_error=original)
        kills = []
        def kill():
            kills.append("inspected-kill")
            raise RuntimeError("kill")
        with self.assertRaises(RuntimeError) as observed:
            reference_smoke.close_owned_reference_child(child, kill)
        self.assertIs(original, observed.exception)
        self.assertEqual(2, len(kills)); self.assertEqual([15, 5], child.drains)
        self.assertIsNone(child.stdin)

    def test_completed_child_is_drained_without_kill_and_partial_child_is_inert(self):
        child = self.Child(completed=True)
        kills = []
        reference_smoke.close_owned_reference_child(child, lambda: kills.append("kill"))
        reference_smoke.close_owned_reference_child(None, lambda: kills.append("kill"))
        self.assertEqual([], kills); self.assertEqual([15], child.drains)
        self.assertEqual(1, child.input_closed)


if __name__ == "__main__":
    unittest.main()
