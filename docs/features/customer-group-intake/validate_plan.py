#!/usr/bin/env python3
"""Offline checks for this planning kit; NOT an application or model evaluation.
Run from any directory: python <feature-dir>/validate_plan.py --self-test
No dependencies outside Python 3.10+ standard library, no network or credentials.
"""
from __future__ import annotations
import argparse
import copy
import json
from pathlib import Path


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def check(backlog: dict, example: dict, cases: list[dict]) -> dict:
    rows = backlog['tasks']
    tasks = {row['id']: row for row in rows}
    require(len(rows) == len(tasks), 'Duplicate task IDs')
    visiting, visited = set(), set()

    def visit(key: str) -> None:
        require(key in tasks, f'Unknown dependency: {key}')
        require(key not in visiting, f'Dependency cycle: {key}')
        if key in visited:
            return
        visiting.add(key)
        for dep in tasks[key]['depends_on']:
            visit(dep)
        visiting.remove(key)
        visited.add(key)

    for key in tasks:
        visit(key)

    def closure(ids: list[str]) -> set[str]:
        result = set()
        def add(key: str) -> None:
            require(key in tasks, f'Unknown profile task: {key}')
            if key in result:
                return
            result.add(key)
            for dep in tasks[key]['depends_on']:
                add(dep)
        for key in ids:
            add(key)
        return result

    for profile, roots in backlog['profiles'].items():
        needed = closure(roots)
        if profile != 'future_auto_reply':
            require(not any(tasks[k]['later'] for k in needed), 'Later work blocks MVP')
        if profile.startswith('mock_'):
            require('GI-01' not in needed, 'Mock profile waits for live account qualification')
    require(example['synthetic'] is True, 'Fixture is not labeled synthetic')
    binding = example['scope']['group_binding_id']
    messages = {m['id']: m for m in example['messages']}
    require(len(messages) == len(example['messages']), 'Duplicate fixture message IDs')
    seqs = [m['seq'] for m in example['messages']]
    require(seqs == sorted(set(seqs)), 'Fixture sequence is not strictly increasing')
    allowed = {'request_evidence', 'needs_review', 'not_actionable', 'system_echo'}
    require(all(m['disposition'] in allowed for m in messages.values()), 'Unclassified fixture message')
    requests = example['expected_requests']
    codes = [r['code'] for r in requests]
    require(len(codes) == len(set(codes)), 'Duplicate request code')
    for request in requests:
        require(request['group_binding_id'] == binding, 'Cross-group request')
        require(bool(request['source_message_ids']), 'Missing request evidence')
        require(all(mid in messages for mid in request['source_message_ids']), 'Unknown source message')
        require(all(messages[mid]['disposition'] == 'request_evidence' for mid in request['source_message_ids']), 'Non-evidence used for request')
        require(request['committed_deadline'] is None, 'Golden fixture invents committed deadline')
    require(all(c['source_message_id'] in messages for c in example['clarifications']), 'Unknown clarification source')
    report = example['expected_report']
    require(report['destination_binding_id'] == binding, 'Wrong destination binding')
    require(report['destination_external_group_id'] == example['scope']['external_group_id'], 'Wrong destination group')
    require(report['message_count'] == 1, 'Digest must be one message in golden fixture')
    require(report['approval_required'] is True, 'Fixture bypasses approval')
    require(report['state'] == 'example_only_not_sent', 'Fixture claims actual send')
    require(set(report['request_codes']) == set(codes), 'Digest request references mismatch')
    require(all(r['internal_note'] not in report['body'] for r in requests), 'Internal note leaked to report')
    require(len({c['id'] for c in cases}) == len(cases), 'Duplicate acceptance case IDs')
    require(all(c['setup'] and c['expected'] and c['status'] == 'to_implement' for c in cases), 'Malformed scenario or falsely executed status')
    return {'tasks': len(rows), 'profiles': len(backlog['profiles']), 'fixture_messages': len(messages), 'fixture_requests': len(requests), 'fixture_report_messages': 1, 'scenario_specs_not_executed': len(cases)}


def negative_tests(backlog: dict, example: dict, cases: list[dict]) -> int:
    mutations = [
        lambda b,e,c: b['tasks'][0]['depends_on'].append('GI-03'),
        lambda b,e,c: b['tasks'][0]['depends_on'].append('GI-99'),
        lambda b,e,c: b['profiles']['mock_it_notes'].append('GI-12'),
        lambda b,e,c: e['expected_requests'][0].update(group_binding_id='binding-demo-b'),
        lambda b,e,c: e['expected_requests'][0]['source_message_ids'].append('missing-message'),
        lambda b,e,c: e['expected_report'].update(destination_external_group_id='group-demo-b'),
        lambda b,e,c: e['expected_report'].update(body=e['expected_report']['body']+e['expected_requests'][0]['internal_note']),
        lambda b,e,c: e['expected_report'].update(message_count=2),
        lambda b,e,c: e['expected_requests'][0].update(committed_deadline='guessed-deadline'),
        lambda b,e,c: c.append(copy.deepcopy(c[0])),
    ]
    for index, mutate in enumerate(mutations, 1):
        b,e,c = copy.deepcopy((backlog,example,cases))
        mutate(b,e,c)
        try:
            check(b,e,c)
        except ValueError:
            continue
        raise ValueError(f'Negative mutation {index} was not rejected')
    return len(mutations)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    try:
        backlog = json.loads((root/'backlog.json').read_text(encoding='utf-8'))
        example = json.loads((root/'examples/golden-batch.json').read_text(encoding='utf-8'))
        cases = [json.loads(line) for line in (root/'acceptance-cases.jsonl').read_text(encoding='utf-8').splitlines() if line.strip()]
        result = check(backlog,example,cases)
        if args.self_test:
            result['negative_mutations_rejected'] = negative_tests(backlog,example,cases)
        print(json.dumps({'status':'pass','scope':'planning_assets_only',**result},ensure_ascii=False,indent=2))
        print('NOT RUN: model evaluation, API/SQL/queue/UI tests, connector login/send or production deployment.')
        return 0
    except (OSError,ValueError,KeyError,TypeError) as exc:
        print(f'FAIL: {exc}')
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
