#!/usr/bin/env python3
"""Offline planning-fixture checks, NOT runtime, AI, SQL or provider tests."""
import argparse
import copy
import json
from pathlib import Path


def need(ok, message):
    if not ok:
        raise ValueError(message)


def validate(b, e, cases):
    p = b['policy']
    need(p['human_approval_required'] is False, 'Human approval gate contradicts owner')
    need(p['trigger'] == 'automatic_event_batch', 'Manual trigger contradicts owner')
    need(p['destination_role'] == 'technical_internal', 'Wrong destination role')
    need(p['customer_group_sending_enabled'] is False, 'Customer sending must remain off')
    need(p['notes_must_be_committed_before_report'] is True, 'Missing commit boundary')
    tasks = {t['id']: t for t in b['tasks']}
    need(len(tasks) == len(b['tasks']), 'Duplicate task ID')
    def closure(ids, stack=()):
        found = set()
        for key in ids:
            need(key in tasks and key not in stack, 'Unknown dependency or cycle')
            found.add(key)
            found.update(closure(tasks[key]['depends_on'], stack + (key,)))
        return found
    closure(list(tasks))
    for name, ids in b['profiles'].items():
        deps = closure(ids)
        if name != 'future_customer_reply':
            need(not any(tasks[x]['later'] for x in deps), 'Later work blocks MVP')
        if name.startswith('mock_'):
            need('GI-01' not in deps, 'Mock waits for live connector')
    need(e['synthetic'] is True and e['human_actions_after_setup'] == [], 'Not an automatic synthetic fixture')
    groups = {g['id']: g for g in e['groups']}
    need(len(groups) == len(e['groups']), 'Duplicate group ID')
    messages = {m['id']: m for m in e['messages']}
    need(len(messages) == len(e['messages']), 'Duplicate message ID')
    routes = {(r['source'], r['destination']) for r in e['routes'] if r['authorized']}
    report = e['expected_report']
    target = report['destination']
    need(target in groups and groups[target]['role'] == 'technical_internal', 'Destination is not internal IT')
    need(report['requires_approval'] is False and report['trigger'] == 'NotesCommitted', 'Incorrect report trigger')
    need(report['customer_replies'] == 0 and report['message_count'] == 1, 'Wrong outbound shape')
    need(report['state'] == 'example_only_not_sent', 'Fixture claims real send')
    notes = {n['id']: n for n in e['expected_notes']}
    need(len(notes) == len(e['expected_notes']), 'Duplicate note ID')
    for n in notes.values():
        source = n['source_group']
        need(source in groups and groups[source]['role'] == 'customer_source', 'Bad source group')
        need(source != target and (source, target) in routes, 'No authorized source-to-IT route')
        need(n['committed'] is True and n['requires_approval'] is False, 'Uncommitted or approval-gated note')
        need(n['committed_deadline'] is None, 'Invented commitment in fixture')
        need(bool(n['message_refs']), 'Missing evidence')
        need(all(m in messages and messages[m]['group'] == source for m in n['message_refs']), 'Invalid/cross-group evidence')
    need(set(report['note_ids']) == set(notes), 'Report loses notes including clarifications')
    need(len({c['id'] for c in cases}) == len(cases), 'Duplicate case ID')
    need(all(c['setup'] and c['expected'] and c['status'] == 'to_implement' for c in cases), 'Invalid case specification')
    return {'tasks': len(tasks), 'fixture_notes': len(notes), 'reports': 1, 'scenario_specs_not_executed': len(cases)}


def self_test(b, e, cases):
    mutations = [
        lambda x,y,z: x['policy'].update(human_approval_required=True),
        lambda x,y,z: x['policy'].update(trigger='manual'),
        lambda x,y,z: x['policy'].update(customer_group_sending_enabled=True),
        lambda x,y,z: y['human_actions_after_setup'].append('approve'),
        lambda x,y,z: y['expected_report'].update(destination='customer-a'),
        lambda x,y,z: y['expected_report'].update(requires_approval=True),
        lambda x,y,z: y['routes'].clear(),
        lambda x,y,z: y['expected_notes'][0].update(committed=False),
        lambda x,y,z: y['expected_notes'][0].update(message_refs=['m4']),
        lambda x,y,z: x['tasks'][0]['depends_on'].append('GI-03'),
    ]
    for i, mutation in enumerate(mutations, 1):
        x,y,z = copy.deepcopy((b,e,cases))
        mutation(x,y,z)
        try:
            validate(x,y,z)
        except ValueError:
            continue
        raise ValueError(f'Negative case {i} not rejected')
    return len(mutations)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    try:
        b = json.loads((root/'backlog.json').read_text(encoding='utf-8'))
        e = json.loads((root/'examples/golden-batch.json').read_text(encoding='utf-8'))
        cases = [json.loads(s) for s in (root/'acceptance-cases.jsonl').read_text(encoding='utf-8').splitlines() if s.strip()]
        result = validate(b,e,cases)
        if args.self_test:
            result['negative_mutations_rejected'] = self_test(b,e,cases)
        print(json.dumps({'status':'pass','scope':'planning_assets_only',**result}, indent=2))
        return 0
    except (OSError,ValueError,KeyError,TypeError) as exc:
        print(f'FAIL: {exc}')
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
