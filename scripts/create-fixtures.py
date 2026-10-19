"""Create synthetic assets only. Never import the user's purchased assets into validation."""
import gzip
import io
import json
from pathlib import Path
import sqlite3
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts' / 'fixtures'
OUT.mkdir(parents=True, exist_ok=True)
GUID_A = 'a' * 32
GUID_B = 'b' * 32
GUID_SCRIPT = 'c' * 32


def package(path, entries):
    with tarfile.open(path, 'w:gz', format=tarfile.USTAR_FORMAT) as tar:
        for guid, name, data in entries:
            parts = [('pathname', name.encode()), ('asset.meta', f'fileFormatVersion: 2\nguid: {guid}\n'.encode())]
            if data is not None:
                parts.insert(1, ('asset', data))
            for leaf, content in parts:
                member = tarfile.TarInfo(f'{guid}/{leaf}')
                member.size = len(content)
                tar.addfile(member, io.BytesIO(content))


package(OUT / 'two-assets.unitypackage', [
    (GUID_A, 'Assets/Generated/ItemA.txt', b'original A\n'),
    (GUID_B, 'Assets/Generated/ItemB.txt', b'original B\n')])
package(OUT / 'script-asset.unitypackage', [
    (GUID_SCRIPT, 'Assets/Generated/ImportedScript.cs', b'public class BoothBridgeFixtureScript {}\n')])
package(OUT / 'bad-path.unitypackage', [(GUID_A, 'Assets/../outside.txt', b'blocked')])
package(OUT / 'shared.unitypackage', [(GUID_A, 'Assets/Generated/ItemA.txt', b'original A\n')])
package(OUT / 'revision.unitypackage', [(GUID_A, 'Assets/Generated/ItemA.txt', b'changed A\n')])
package(OUT / 'folder-only.unitypackage', [('f' * 32, 'Assets/Generated', None)])
with gzip.open(OUT / 'bad-header.unitypackage', 'wb') as stream:
    stream.write(b'x' * 512)
with zipfile.ZipFile(OUT / 'nested.zip', 'w') as archive:
    archive.write(OUT / 'two-assets.unitypackage', '../nested/two-assets.unitypackage')

# Model the observed BLM 0.5.0 schema, without copying any personal database values.
db_path = OUT / 'fixture.db'
if db_path.exists():
    db_path.unlink()
with sqlite3.connect(db_path) as db:
    db.executescript('''
      CREATE TABLE schema_version(version INTEGER);
      INSERT INTO schema_version VALUES(20261005);
      CREATE TABLE preferences(item_directory_path BLOB);
      CREATE TABLE registered_items(id TEXT,booth_item_id INTEGER,user_item_info_id INTEGER,created_at TEXT);
      CREATE TABLE booth_items(id INTEGER,name TEXT,shop_subdomain TEXT,thumbnail_url TEXT);
      CREATE TABLE overwritten_booth_items(booth_item_id INTEGER,name TEXT);
      CREATE TABLE shops(subdomain TEXT,name TEXT);
      CREATE TABLE user_item_info(id INTEGER,name TEXT,shop_name TEXT,thumbnail_filename TEXT);
      INSERT INTO shops VALUES('fixture','検証用ショップ');
      INSERT INTO booth_items VALUES(123,'テスト商品','fixture',NULL);
      INSERT INTO overwritten_booth_items VALUES(123,'上書き商品名');
      INSERT INTO registered_items VALUES('b123',123,NULL,'2026-10-18');
      INSERT INTO user_item_info VALUES(1,'手動商品','手動ショップ','thumb.png');
      INSERT INTO registered_items VALUES('u1',NULL,1,'2026-10-18');
    ''')
    db.execute('INSERT INTO preferences VALUES(?)', (str(OUT / 'ライブラリ').encode('utf-16-le'),))
print(json.dumps({'fixtures': str(OUT), 'package_assets': 2}, ensure_ascii=False))
