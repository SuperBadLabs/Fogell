#!/usr/bin/env python3
"""Reject retired language dependencies and missing solution inputs."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
retired = re.compile(r'Fogell\.(?:Groovy|Differential)|Jenkinsfile|x-jenkinsfile|FParsec', re.I)
errors = []
for folder in ('src', 'tools', 'tests'):
    for path in (root / folder).rglob('*'):
        if {'bin', 'obj'}.intersection(path.parts) or not path.is_file():
            continue
        if path.suffix in ('.fs', '.fsproj', '.json') and retired.search(path.read_text()):
            errors.append(f'retired dependency in {path.relative_to(root)}')
for project in ET.parse(root / 'Fogell.slnx').iter('Project'):
    path = root / project.attrib['Path']
    if not path.is_file():
        errors.append(f'missing project: {path}')
        continue
    for item in ET.parse(path).iter():
        if item.tag in ('Compile', 'ProjectReference'):
            if not (path.parent / item.attrib['Include']).is_file():
                errors.append(f'missing {item.tag} in {path}')
if errors:
    raise SystemExit('\n'.join(errors))
print('PASS native source/dependency inventory')
