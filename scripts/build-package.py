"""Assign stable Unity GUIDs and build local-install ZIP and UPM tarball artifacts."""
from pathlib import Path
import hashlib
import io
import json
import tarfile
import uuid
import zipfile
import gzip
import shutil
import re

root = Path(__file__).resolve().parents[1]
package = root / 'Packages' / 'io.github.kokokaiyokan.bamu'
out = root / 'artifacts'
out.mkdir(exist_ok=True)

# Catch release-version drift before building the bundled extension or archives.
version = json.loads((package / 'package.json').read_text(encoding='utf-8'))['version']
core_version = re.search(r'const string Version = "([^"]+)";', (package / 'Editor' / 'UnityBridgeCore.cs').read_text(encoding='utf-8')).group(1)
extension_version = json.loads((root / 'browser-extension' / 'manifest.json').read_text(encoding='utf-8'))['version']
if not version == core_version == extension_version:
    raise SystemExit('Version mismatch: package=' + version + ', receiver=' + core_version + ', extension=' + extension_version)


# Bundle only the browser extension. The receiver is C# inside Unity; legacy
# Python sources remain in the development repository and are never shipped.
tools = package / 'Tools'
(package / 'Documentation~').mkdir(exist_ok=True)
for document in ('booth-sync.md', 'prefab-converter-modules.md'):
    shutil.copyfile(root / 'docs' / document, package / 'Documentation~' / document)
(tools / 'browser-extension').mkdir(parents=True, exist_ok=True)
for source in (root / 'browser-extension').iterdir():
    if source.is_file() and source.name != 'package.json':
        shutil.copyfile(source, tools / 'browser-extension' / source.name)

# Refresh the legacy unpacked-extension directory only if it already exists locally.
legacy_extension = root / 'Packages' / 'pm.booth.unity-bridge' / 'Tools' / 'browser-extension'
if legacy_extension.is_dir():
    for source in (root / 'browser-extension').iterdir():
        if source.is_file() and source.name != 'package.json':
            shutil.copyfile(source, legacy_extension / source.name)

# Keep these checked in; moving the source package must not regenerate script GUIDs.
for path in sorted(package.rglob('*')):
    if path.suffix == '.meta' or path.name.startswith('.') or '__pycache__' in path.parts or path.name == '__pycache__.meta' or 'companion' in path.parts or 'LocalModules' in path.parts or path.name == 'LocalModules.meta':
        continue
    meta = path.with_name(path.name + '.meta')
    if meta.exists():
        continue
    guid = uuid.uuid5(uuid.NAMESPACE_URL, 'bamu/' + path.relative_to(package).as_posix()).hex
    text = f'fileFormatVersion: 2\nguid: {guid}\n'
    if path.is_dir():
        text += 'folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    elif path.suffix == '.cs':
        text += 'MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    elif path.suffix == '.asmdef':
        text += 'AssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    else:
        text += 'DefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    meta.write_text(text, encoding='utf-8')

version = json.loads((package / 'package.json').read_text(encoding='utf-8'))['version']
files = sorted(p for p in package.rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.name != '__pycache__.meta' and 'companion' not in p.parts and p.name != 'companion.meta' and 'LocalModules' not in p.parts and p.name != 'LocalModules.meta')
zip_path = out / f'bamu-{version}.zip'
with zipfile.ZipFile(zip_path, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    for path in files:
        info = zipfile.ZipInfo(path.relative_to(package).as_posix(), date_time=(2026, 10, 18, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(info, path.read_bytes())
tgz_path = out / f'bamu-{version}.tgz'
with tgz_path.open('wb') as output:
    with gzip.GzipFile(fileobj=output, mode='wb', filename='', mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode='w', format=tarfile.USTAR_FORMAT) as archive:
            for path in files:
                data = path.read_bytes()
                member = tarfile.TarInfo('package/' + path.relative_to(package).as_posix())
                member.size = len(data)
                member.mode = 0o644
                archive.addfile(member, io.BytesIO(data))
hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (zip_path, tgz_path)}
(out / 'package-checksums.json').write_text(json.dumps(hashes, indent=2), encoding='utf-8')
print(json.dumps({'version': version, 'files': len(files), 'artifacts': hashes}, indent=2))
