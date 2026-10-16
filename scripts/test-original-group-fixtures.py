"""Cold original-cohort controls. No SQL, Docker or model qualification."""
import ast
import copy
import importlib.util
import os
from pathlib import Path
import re
import unittest
from unittest.mock import patch
import uuid


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


roles = load('original_fixture_tests', 'owned-original-group-fixtures.py')
terminal = load('original_fixture_terminal_tests', 'smoke-group-terminal-receipts.py')
NAMES = ('tenantId', 'companyId', 'serviceId', 'sourceId', 'eventId', 'batchId')


def candidates(rows):
    return [dict(row=row, label=label, outcome=outcome, count=count, terminal=True,
        provider='synthetic', role=1, enabled=1, version=1, deletion=0,
        manifest_version=1, manifest_bytes=274, selected=2, prefix='0200')
        for row, (_, label, outcome, count) in zip(rows, roles.ROLES.values())]


def cold_frames(query, records):
    """Interpret the actual closed scope/role predicates, never execute SQL."""
    tenant, company = re.search(r"WHERE w.TenantId='([^']+)' AND w.CompanyId='([^']+)'", query).groups()
    ordinals = {label: int(number) for label, number in re.findall(r"WHEN N'([^']+)' THEN (\d+)", query)}
    signatures = set((label, int(a), int(b)) for label, a, b in re.findall(
        r"\(b.DisplayName COLLATE Latin1_General_100_BIN2=N'([^']+)' AND w.Outcome=(\d+) AND w.NoteCount=(\d+)\)", query))
    broad = set((int(a), int(b)) for a,b in re.findall(r'w\.Outcome=(\d+) AND w\.NoteCount=(\d+)', query))
    for predicate in ("SELECT TOP(5)", "b.TenantId=w.TenantId AND b.CompanyId=w.CompanyId AND b.Id=w.BindingId",
            "a.TenantId=w.TenantId AND a.CompanyId=w.CompanyId AND a.BindingId=w.BindingId AND a.Id=w.BatchId",
            "b.Provider=N'synthetic' AND b.Role=1 AND b.IsEnabled=1 AND b.Version=1 AND b.DeletionGeneration=0",
            "w.DependencyManifestVersion=1", "DATALENGTH(w.DependencyManifest)=274", "w.SelectedMessageCount=2",
            "SUBSTRING(w.DependencyManifest,161,2)=0x0200"):
        assert predicate in query
    require_terminal = 'JOIN aioffice.GroupBatchTerminalReceipts' in query
    if require_terminal:
        assert 't.TenantId=w.TenantId AND t.CompanyId=w.CompanyId AND t.BindingId=w.BindingId AND t.BatchId=w.BatchId' in query
    selected = []
    for item in records:
        row = item['row']
        if (row['tenantId'],row['companyId']) != (tenant,company): continue
        if tuple(item[name] for name in ('provider','role','enabled','version','deletion','manifest_version','manifest_bytes','selected','prefix')) != ('synthetic',1,1,1,0,1,274,2,'0200'): continue
        if require_terminal and not item['terminal']: continue
        if signatures:
            if (item['label'],item['outcome'],item['count']) not in signatures: continue
        elif (item['outcome'],item['count']) not in broad: continue
        selected.append((ordinals.get(item['label'],0),row))
    selected.sort(key=lambda item:(item[0],item[1]['sourceId']))
    frames = [(ordinal,field+1,row['sourceId'],row[name]) for ordinal,row in selected[:5] for field,name in enumerate(NAMES)]
    frames.sort(key=lambda item:item[:3])
    return '\n'.join(f'{ordinal}:{field}:{value}' for ordinal,field,_,value in frames)


class OriginalGroupFixturesTests(unittest.TestCase):
    def setUp(self):
        self.tenant,self.company = (str(uuid.uuid4()) for _ in range(2))
        self.rows = [dict(tenantId=self.tenant,companyId=self.company,**{name:str(uuid.uuid4()) for name in NAMES[2:]}) for _ in range(4)]
        self.directory = Path(__file__).resolve().parent/'owned-private-runner'/'aioffice-local'
        self.environment = dict(CI='true',GITHUB_ACTIONS='true',RUNNER_TEMP=str(self.directory.parent))
        self.arguments = dict(directory=self.directory,api='http://127.0.0.1:8080',tenant=self.tenant,
            company=self.company,source=self.rows[0]['sourceId'],slot=4)

    def test_originals_and_unrelated_history_or_exhaustion_select_only_four_in_both_phases(self):
        records = candidates(self.rows)
        for outcome,count in ((1,4),(3,3)):
            item = copy.deepcopy(records[0]); item.update(label='Owned clean SQL effect fixture',outcome=outcome,count=count)
            item['row'].update({name:str(uuid.uuid4()) for name in NAMES[2:]}); records.append(item)
        for terminal_join in (False,True):
            text = cold_frames(roles.frames_query(self.tenant,self.company,require_terminal=terminal_join),records)
            self.assertEqual(self.rows,terminal.require_fixture_frames(text,self.tenant,self.company))

    def test_fifth_original_and_duplicate_balanced_by_missing_role_are_refused(self):
        records = candidates(self.rows)
        extra = copy.deepcopy(records[0]); extra['row'].update({name:str(uuid.uuid4()) for name in NAMES[2:]})
        for values in ([*records,extra],[records[0],extra,*records[2:]],records[:3]):
            for terminal_join in (False,True):
                with self.subTest(count=len(values),terminal=terminal_join), self.assertRaises(AssertionError):
                    terminal.require_fixture_frames(cold_frames(roles.frames_query(self.tenant,self.company,require_terminal=terminal_join),values),self.tenant,self.company)

    def test_wrong_signature_scope_ownership_or_manifest_cannot_replace_original(self):
        faults = [('outcome',3),('count',3),('label','Owned clean SQL effect fixture'),('provider','other'),
            ('role',2),('enabled',0),('version',2),('deletion',1),('manifest_version',2),('manifest_bytes',275),('selected',3),('prefix','0300')]
        for name,value in faults + [('tenantId',str(uuid.uuid4())),('companyId',str(uuid.uuid4()))]:
            records = candidates(self.rows)
            (records[0]['row'] if name in NAMES else records[0])[name] = value
            with self.subTest(name=name), self.assertRaises(AssertionError):
                terminal.require_fixture_frames(cold_frames(roles.frames_query(self.tenant,self.company),records),self.tenant,self.company)

    def test_missing_scoped_terminal_is_not_replaced_by_foreign_terminal(self):
        records = candidates(self.rows); records[0]['terminal'] = False
        foreign = copy.deepcopy(records[0]); foreign['row']['companyId'] = str(uuid.uuid4()); foreign['terminal'] = True
        records.append(foreign)
        self.assertEqual(self.rows,terminal.require_fixture_frames(cold_frames(roles.frames_query(self.tenant,self.company),records),self.tenant,self.company))
        with self.assertRaises(AssertionError):
            terminal.require_fixture_frames(cold_frames(roles.frames_query(self.tenant,self.company,require_terminal=True),records),self.tenant,self.company)

    def test_unowned_guard_precedes_scope_validation_and_sql(self):
        def forbidden(query): self.fail('Unowned enrollment touched SQL')
        for invalid in ({'CI':'false'},{'GITHUB_ACTIONS':'false'},{'RUNNER_TEMP':''}):
            with patch.dict(os.environ,{**self.environment,**invalid},clear=True),self.assertRaises(RuntimeError):
                roles.enroll(**{**self.arguments,'tenant':None,'sql':forbidden})

    def test_invalid_slot_and_scope_refuse_before_sql(self):
        def forbidden(query): self.fail('Invalid enrollment touched SQL')
        for change in ({'slot':True},{'slot':15},{'slot':'4'},{'tenant':None},{'company':str(uuid.UUID(int=0))},
                {'source':'ABCDEFAB-1234-4321-ABCD-ABCDEFABCDEF'},{'source':"';DROP TABLE x;"}):
            with self.subTest(change=change),patch.dict(os.environ,self.environment,clear=True),self.assertRaises((AssertionError,ValueError)):
                roles.enroll(**{**self.arguments,**change,'sql':forbidden})

    def test_only_original_slots_stamp_closed_labels_before_any_events_or_claims(self):
        calls = []
        def sql(query): calls.append(query); return '1'
        with patch.dict(os.environ,self.environment,clear=True):
            for slot in (*range(1,15),16): roles.enroll(**{**self.arguments,'slot':slot,'sql':sql})
        self.assertEqual(4,len(calls))
        for query,(_,label,_,_) in zip(calls,roles.ROLES.values()):
            self.assertIn("SET NOCOUNT ON; UPDATE aioffice.GroupBindings SET DisplayName=N'"+label+"'",query)
            self.assertIn(f"WHERE TenantId='{self.tenant}' AND CompanyId='{self.company}' AND Id='{self.arguments['source']}'",query)
            self.assertIn("AND DisplayName=N'Owned clean SQL effect fixture'",query)
            self.assertIn("AND Provider=N'synthetic' AND Role=1 AND IsEnabled=1 AND Version=1 AND DeletionGeneration=0",query)
            for table in ('GroupMessages','GroupIngressReceipts','GroupSourceStates','GroupBatchAllocations','GroupBatchClaimReceipts'):
                self.assertIn(f"AND NOT EXISTS(SELECT 1 FROM aioffice.{table} WHERE TenantId='{self.tenant}' AND CompanyId='{self.company}' AND BindingId='{self.arguments['source']}')",query)
            self.assertTrue(query.endswith('; SELECT @@ROWCOUNT;'))

    def test_unavailable_or_malformed_enrollment_never_qualifies(self):
        for result in ('0','2','','1\n','private error',1,None):
            with self.subTest(result=result),patch.dict(os.environ,self.environment,clear=True),self.assertRaisesRegex(AssertionError,'^Owned original fixture enrollment unavailable$'):
                roles.enroll(**self.arguments,sql=lambda query:result)

    def test_failed_enrollment_does_not_write_configuration_or_recreate_core(self):
        ingress = load('original_fixture_ingress_tests','smoke-group-ingress.py')
        tree = ast.parse(Path(ingress.__file__).read_text(encoding='utf-8'))
        callback = next(node for node in ast.walk(tree) if isinstance(node,ast.FunctionDef) and node.name=='enroll_owned_source')
        namespace = dict(uuid=uuid,importlib=importlib,Path=Path,__file__=ingress.__file__,tenant=self.tenant,company=self.company,
            directory=self.directory,api=self.arguments['api'],private_environment={},sql=lambda query:'0')
        exec(compile(ast.fix_missing_locations(ast.Module([callback],type_ignores=[])),'<inert-original-enrollment>','exec'),namespace)
        for slot in (4,5,6,8):
            with patch.dict(os.environ,self.environment,clear=True),self.assertRaisesRegex(AssertionError,'enrollment unavailable'):
                namespace['enroll_owned_source'](self.arguments['source'],slot)
            self.assertEqual({},namespace['private_environment'])


if __name__ == '__main__': unittest.main()
