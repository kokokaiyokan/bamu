"""Compile against the installed Unity Editor's reference assemblies without starting Editor."""
import argparse
from pathlib import Path
import subprocess
import json

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--unity-data', type=Path, default=Path(r'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Data'))
parser.add_argument('--with-local-modules', action='store_true', help='Also compile optional machine-local modules against the core public API')
args = parser.parse_args()
out = root.parent / 'bamu_development' / 'artifacts' / 'compile'
out.mkdir(parents=True, exist_ok=True)
sdk_line = subprocess.check_output(['dotnet', '--list-sdks'], text=True).strip().splitlines()[-1]
version, location = sdk_line.split(' ', 1)
csc = Path(location.strip('[]')) / version / 'Roslyn' / 'bincore' / 'csc.dll'
refs = list((args.unity_data / 'NetStandard' / 'ref' / '2.1.0').glob('*.dll'))
refs += list((args.unity_data / 'Managed' / 'UnityEngine').glob('*.dll'))


def compile_to(name, sources, extra_refs=()):
    response = out / (name + '.rsp')
    dll = out / (name + '.dll')
    lines = ['-nologo', '-utf8output', '-nostdlib+', '-target:library', '-langversion:9.0', '-warn:4',
             f'-out:"{dll}"']
    lines += [f'-r:"{p}"' for p in [*refs, *extra_refs]]
    lines += [f'"{p}"' for p in sources]
    response.write_text('\n'.join(lines), encoding='utf-8-sig')
    result = subprocess.run(['dotnet', str(csc), '@' + str(response)], capture_output=True, text=True, encoding='utf-8', errors='replace')
    print(result.stdout, end='')
    print(result.stderr, end='')
    if result.returncode:
        raise SystemExit(result.returncode)
    return dll


package = compile_to('bamu.Editor', sorted((root / 'Packages' / 'io.github.kokokaiyokan.bamu' / 'Editor').glob('*.cs')))
local_modules = []
if args.with_local_modules:
    local = root / 'Packages' / 'io.github.kokokaiyokan.bamu' / 'LocalModules'
    for definition in sorted(local.rglob('*.asmdef')):
        name = json.loads(definition.read_text(encoding='utf-8'))['name']
        compile_to(name, sorted(definition.parent.rglob('*.cs')), [package])
        local_modules.append(name)
compile_to('BridgeValidation', [root / 'validation-project' / 'Assets' / 'Editor' / 'BridgeValidation.cs'], [package])
print(json.dumps({'status': 'compiled', 'unity_references': str(args.unity_data), 'package': str(package), 'local_modules': local_modules}))
