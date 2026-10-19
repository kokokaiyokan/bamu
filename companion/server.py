"""Local BOOTH/BLM update companion. Python 3.10+, standard library only.

BOOTH authentication stays in Chrome. This process reads the BLM database in
read-only mode and observes downloads performed by BLM itself.
It never modifies Unity assets, the BLM database, or BLM item files.
"""
from __future__ import annotations
import argparse
import copy
from contextlib import closing
import hashlib
import hmac
import json
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import shutil
import sqlite3
import stat
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit
import uuid
import zipfile
import tarfile
from blm_handoff import BlmHandoff
from catalog_policy import CatalogPolicy

VERSION = '0.8.0'
PORT = 48763
MAX_FILE = 4 * 1024**3
MAX_EXPANDED = 20 * 1024**3
MAX_FILES = 100_000


def utc():
    return time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())


def atomic_json(path: Path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        with temporary.open('x', encoding='utf-8') as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def sha256(path: Path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


def inside(path: Path, parent: Path):
    try:
        path.resolve().relative_to(parent.resolve())
        return True
    except (ValueError, OSError):
        return False


def safe_name(value):
    value = str(value)
    if not value or value in ('.', '..') or any(c in value for c in '<>"/\\:|?*\x00') or any(ord(c) < 32 for c in value):
        raise ValueError('不正なファイル名です。')
    if value.rstrip(' .') != value or re.fullmatch(r'(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?', value, re.I):
        raise ValueError('Windowsで使用できないファイル名です。')
    return value


def valid_file(record):
    item, file_id, variation = (str(record.get(k, '')) for k in ('item_id', 'file_id', 'variation_id'))
    if not all(re.fullmatch(r'[1-9][0-9]{0,17}', v) for v in (item, file_id, variation)):
        raise ValueError('BOOTHのファイル識別子が不正です。')
    name = safe_name(record.get('name', ''))
    # Rebuild only the observed, credential-free official download endpoint.
    return dict(item_id=item, file_id=file_id, variation_id=variation, name=name,
                key=f'{item}:{variation}:{file_id}', url=f'https://booth.pm/downloadables/{file_id}/deeplink?client=booth-library-manager&variation_id={variation}',
                item_name=str(record.get('item_name', ''))[:300])


def read_library(database: Path, root_override=''):
    with closing(sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True, timeout=2)) as db:
        db.execute('BEGIN')
        schema = db.execute('SELECT version FROM schema_version').fetchone()[0]
        if schema != 20261005:
            raise ValueError(f'未対応のBLMスキーマです: {schema}')
        raw = db.execute('SELECT item_directory_path FROM preferences LIMIT 1').fetchone()[0]
        root = Path(root_override or (raw.decode('utf-16-le').rstrip('\x00') if isinstance(raw, bytes) else raw))
        items = {}
        for registration, item_id, name in db.execute('''
            SELECT r.id,r.booth_item_id,COALESCE(o.name,b.name)
            FROM registered_items r JOIN booth_items b ON b.id=r.booth_item_id
            LEFT JOIN overwritten_booth_items o ON o.booth_item_id=b.id'''):
            target = root / safe_name(registration)
            if not inside(target, root):
                raise ValueError('BLMの商品保存先がライブラリ外を参照しています。')
            items[str(item_id)] = dict(item_id=str(item_id), name=name, directory=str(target))
        return root, items


class Companion(CatalogPolicy, BlmHandoff):
    def __init__(self, state_dir: Path, database: Path, downloads: Path, library_root='', port=PORT):
        self.home, self.database, self.downloads = state_dir.resolve(), database.resolve(), downloads.resolve()
        self.library_root, self.port = library_root, port
        self.home.mkdir(parents=True, exist_ok=True)
        self.lock = threading.RLock()
        self.observer_lock = threading.Lock()
        self.processing = set()
        connection = self.home / 'connection.json'
        config = json.loads(connection.read_text(encoding='utf-8')) if connection.exists() else {'token': secrets.token_urlsafe(32)}
        self.token = config['token']
        atomic_json(connection, dict(port=port, token=self.token))
        path = self.home / 'state.json'
        self.state = json.loads(path.read_text(encoding='utf-8')) if path.exists() else dict(schema_version=1, watches={}, files={}, jobs=[],
            last_catalog=0, requested=True, interval_hours=6, verify_hours=0, browser_status='ブラウザ拡張の接続待ち', revisions=[])
        if self.state.get('schema_version') != 1:
            raise ValueError('未対応の補助プログラム設定です。')
        # Preserve dispatched BLM observations; never redispatch blindly after restart.
        if self.state.get('transport') != 'blm': self.state['verify_hours'] = 0
        for job in self.state['jobs']:
            if job['state'] in ('Downloading', 'Processing') or (job.get('transport') != 'blm' and job['state'] in ('Queued','Retry')):
                job.update(state='Cancelled', message='旧取得方式の未完了ジョブを停止しました。')
            elif job['state'] == 'Preparing':
                job.update(state='Retry',retry_at=time.time()+60)
        self.state['transport'] = 'blm'
        self.state['verify_hours'] = 0  # Never download just to check whether bytes changed.
        self.state['auto_enabled']=False
        # Stop legacy automatic work; observe already-dispatched native downloads only.
        for job in self.state['jobs']:
            if job.get('source')!='manual_missing' and job['state'] in ('Queued','Retry','Preparing','ReadyForBLM','NeedsAttention'):
                job.update(state='Cancelled',message='自動取得を廃止したため未送信の取得を停止しました。')
        self.state.setdefault('metadata',{})
        if self.state.get('tag_parser_version') != 1:
            for metadata in self.state['metadata'].values():
                if metadata.get('tag_source_version') != 1: metadata['fetched_at'] = 0
            self.state['tag_parser_version'] = 1
        for watch in self.state['watches'].values():
            watch.setdefault('target_mode','include')  # Preserve previous explicit selections.
            watch.setdefault('selection_mode','manual')
        self.save()

    def read_library(self):
        return read_library(self.database, self.library_root)

    def save(self):
        atomic_json(self.home / 'state.json', self.state)

    def status(self):
        with self.lock:
            root, products = self.products()
            value = copy.deepcopy(self.state)
            # Fixed-shape arrays are easy to consume from JsonUtility as well.
            return dict(version=VERSION, products=list(products.values()), files=list(value['files'].values()),
                jobs=value['jobs'][-100:], revisions=value['revisions'], watches=list(value['watches'].values()),
                requested=value['requested'], last_catalog=value['last_catalog'], interval_hours=value['interval_hours'],
                verify_hours=value['verify_hours'], browser_status=value['browser_status'],
                auto_enabled=value['auto_enabled'],metadata_requests=self.metadata_requests(),transport='blm', library_root=str(root))

    def configure(self, payload):
        with self.lock:
            _, products = self.products()
            item = str(payload.get('item_id', ''))
            if item not in products:
                raise ValueError('BOOTHのライブラリで確認した商品を指定してください。')
            keys = list(dict.fromkeys(str(k) for k in payload.get('file_keys', [])))
            if any(k not in self.state['files'] or self.state['files'][k]['item_id'] != item for k in keys):
                raise ValueError('一度BOOTHのファイル一覧を確認してから選択してください。')
            enabled = payload.get('enabled') is True
            target_mode=payload.get('target_mode','include')
            selection_mode=payload.get('selection_mode','manual')
            if target_mode not in ('auto','include','exclude') or selection_mode not in ('auto','manual'):
                raise ValueError('対象指定・ファイル選択の設定が不正です。')
            variations = sorted({self.state['files'][k]['variation_id'] for k in keys})
            self.state['watches'][item] = dict(item_id=item, enabled=enabled, file_keys=keys, variation_ids=variations,
                target_mode=target_mode,selection_mode=selection_mode,include_new_files=payload.get('include_new_files') is True)
            self.queue_due()
            self.state['requested'] = True
            self.save()
            return {'ok': True}

    def catalog(self, records, complete=True):
        # Validate the whole submission before applying it.
        incoming = {r['key']: r for r in (valid_file(r) for r in records)}
        with self.lock:
            self.read_library()  # Confirm the local BLM schema before accepting a catalog.
            for key, record in incoming.items():
                previous = self.state['files'].get(key, {})
                self.state['files'][key] = {**previous, **record, 'last_seen': utc(), 'available': True}
                if not previous and self.state.get('inventory_initialized'):
                    self.state['files'][key]['remote_added'] = True
            self.state['inventory_initialized'] = True
            if complete:
                for key, record in self.state['files'].items():
                    record['available'] = key in incoming
            self.state['last_catalog'] = time.time()
            self.state['requested'] = False
            self.state['browser_status'] = 'BOOTHファイル一覧を確認済み' if complete else 'ページ上限まで確認（未確認のページあり）'
            self.refresh_manual_choices()
            self.queue_due()
            self.save()
            return {'ok': True, 'known_files': len(self.state['files'])}

    def queue_due(self):
        pass  # Catalog and settings updates never schedule downloads.

    def fail(self, job_id, message='取得に失敗しました。'):
        with self.lock:
            job = self.job(job_id)
            if job['state'] in ('Saved', 'Unchanged', 'Cancelled'):
                return {'ok': True}
            job['state'] = 'NeedsAttention' if job['state'] in ('AwaitingBLM', 'Verifying') else ('Failed' if job['attempts'] >= 5 else 'Retry')
            job['retry_at'] = time.time() + min(3600, 60 * 2**min(job['attempts'], 5))
            # The extension sends a generic code, never response bodies/URLs.
            job['message'] = str(message)[:180]
            self.save()
            return {'ok': True}

    def job(self, job_id):
        return next(j for j in self.state['jobs'] if j['id'] == job_id)

    @staticmethod
    def verify_receipt(target, receipt):
        for entry in receipt['files']:
            file = target / entry['name']
            if not inside(file, target) or not file.is_file() or sha256(file) != entry['sha256']:
                raise ValueError('保存済みの版に変更があります。上書きせず停止しました。')

    @staticmethod
    def unpack(archive: Path, staging: Path):
        entries = []
        with archive.open('rb') as stream:
            prefix = stream.read(512).lstrip().lower()
        if prefix.startswith((b'<!doctype html', b'<html')):
            raise ValueError('商品ファイルの代わりにHTMLが取得されています。')
        if archive.suffix.lower() != '.zip':
            if archive.suffix.lower() == '.unitypackage':
                # Read through the gzip/TAR to reject truncated or HTML responses.
                with tarfile.open(archive, 'r|gz') as tar:
                    found = False; expanded = 0
                    for member in tar:
                        expanded += member.size
                        if expanded > MAX_EXPANDED: raise ValueError('パッケージの展開サイズが上限を超えています。')
                        if member.name.endswith('/pathname'): found = True
                    if not found:
                        raise ValueError('unitypackageにパス情報がありません。')
            destination = staging / safe_name(archive.name)
            shutil.copyfile(archive, destination)
            return [dict(name=destination.name, sha256=sha256(destination), size=destination.stat().st_size)]
        with zipfile.ZipFile(archive) as bundle:
            if len(bundle.infolist()) > MAX_FILES or sum(m.file_size for m in bundle.infolist()) > MAX_EXPANDED:
                raise ValueError('ZIPの展開サイズが上限を超えています。')
            seen = set()
            for member in bundle.infolist():
                parts = PurePosixPath(member.filename.replace('\\', '/')).parts
                if not parts or member.filename.startswith(('/', '\\')) or any(p in ('.', '..') for p in parts):
                    raise ValueError('ZIP内のパスが不正です。')
                for part in parts:
                    safe_name(part)
                relative = '/'.join(parts)
                if relative.casefold() == '.bridge-receipt.json':
                    raise ValueError('ZIPが補助プログラムの管理ファイル名と競合しています。')
                if relative.casefold() in seen:
                    raise ValueError('ZIP内に重複したパスがあります。')
                seen.add(relative.casefold())
                if stat.S_ISLNK(member.external_attr >> 16):
                    raise ValueError('ZIP内のリンクは展開できません。')
                destination = staging.joinpath(*parts)
                if not inside(destination, staging):
                    raise ValueError('ZIPが保存先外を参照しています。')
                if member.is_dir():
                    destination.mkdir(parents=True, exist_ok=True); continue
                destination.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(member) as src, destination.open('xb') as dst:
                    shutil.copyfileobj(src, dst, 1024 * 1024)  # CRC verified by ZipExtFile.
                entries.append(dict(name=relative, sha256=sha256(destination), size=destination.stat().st_size))
        if not entries:
            raise ValueError('空のZIPです。')
        return entries


def make_server(companion: Companion):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass  # No tokens, download paths or request payloads in access logs.

        def authorized(self):
            host = self.headers.get('Host', '')
            origin = self.headers.get('Origin', '')
            if host != f'127.0.0.1:{companion.port}' or (origin and not re.fullmatch(r'chrome-extension://[a-p]{32}', origin)):
                return False
            token = self.headers.get('Authorization', '').removeprefix('Bearer ')
            return hmac.compare_digest(token, companion.token)

        def reply(self, code, value):
            data = json.dumps(value, ensure_ascii=False).encode('utf-8')
            self.send_response(code)
            origin = self.headers.get('Origin', '')
            if re.fullmatch(r'chrome-extension://[a-p]{32}', origin):
                self.send_header('Access-Control-Allow-Origin', origin)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.send_header('Cache-Control', 'no-store')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers(); self.wfile.write(data)

        def do_OPTIONS(self):
            origin = self.headers.get('Origin', '')
            if not re.fullmatch(r'chrome-extension://[a-p]{32}', origin):
                self.reply(403, {'error': 'Origin rejected'}); return
            self.send_response(204)
            self.send_header('Access-Control-Allow-Origin', origin)
            self.send_header('Access-Control-Allow-Headers', 'Authorization, Content-Type')
            self.send_header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS')
            self.end_headers()

        def do_GET(self):
            if not self.authorized():
                self.reply(403, {'error': '接続キーを確認してください。'}); return
            try:
                if self.path == '/v1/status': self.reply(200, companion.status())
                else: self.reply(404, {'error': 'Not found'})
            except Exception: self.reply(500, {'error': 'BLMのDBまたは補助プログラムの状態を確認してください。'})

        def do_POST(self):
            if not self.authorized():
                self.reply(403, {'error': '接続キーを確認してください。'}); return
            try:
                length = int(self.headers.get('Content-Length', '0'))
                if length < 0 or length > 4 * 1024 * 1024:
                    raise ValueError('リクエストが大きすぎます。')
                body = json.loads(self.rfile.read(length).decode('utf-8'))
                if self.path == '/v1/watch': result = companion.configure(body)
                elif self.path == '/v1/catalog': result = companion.catalog(body.get('files', []), body.get('complete') is True)
                elif self.path == '/v1/metadata': result = companion.update_metadata(body.get('products',[]))
                elif self.path == '/v1/bulk-missing': result = companion.bulk_missing()
                elif self.path == '/v1/next': result = companion.next_job()
                elif self.path == '/v1/retry': result = companion.retry_blm(body['job_id'])
                elif self.path == '/v1/dismiss': result = companion.dismiss_blm(body['job_id'])
                elif self.path == '/v1/failed': result = companion.fail(body['job_id'], 'BLMへの引き渡しを確認できません。ブラウザとBLMを確認してください。')
                elif self.path == '/v1/check':
                    with companion.lock:
                        companion.state['requested'] = True; companion.save()
                    result = {'ok': True}
                elif self.path == '/v1/shutdown':
                    with companion.lock:
                        if companion.processing:
                            raise ValueError('保存処理中です。完了後に停止してください。')
                    self.reply(200, {'ok': True})
                    threading.Thread(target=self.server.shutdown, daemon=True).start()
                    return
                elif self.path == '/v1/settings':
                    with companion.lock:
                        companion.state['interval_hours'] = max(1, min(168, int(body.get('interval_hours', 6))))
                        companion.state['verify_hours'] = 0
                        companion.state['auto_enabled']=False
                        companion.queue_due()
                        companion.save()
                    result = {'ok': True}
                elif self.path == '/v1/browser-status':
                    with companion.lock:
                        codes = {'login': 'BOOTHへのログインが必要です。', 'rate_limit': 'BOOTHの制限により待機中。', 'error': 'ブラウザ側の確認に失敗しました。', 'connected': 'ブラウザ拡張が接続しました。'}
                        companion.state['browser_status'] = codes.get(body.get('code'), 'ブラウザ拡張の状態を確認してください。'); companion.save()
                    result = {'ok': True}
                else:
                    self.reply(404, {'error': 'Not found'}); return
                self.reply(200, result)
            except (ValueError, KeyError, StopIteration) as ex:
                self.reply(400, {'error': str(ex)[:180] or '操作対象が見つかりません。'})
            except Exception:
                self.reply(500, {'error': 'ローカル保存処理に失敗しました。設定と保存先を確認してください。'})

    return ThreadingHTTPServer(('127.0.0.1', companion.port), Handler)


def serve(companion: Companion):
    server = make_server(companion)
    print(f'BOOTH Unity Bridge {VERSION}: http://127.0.0.1:{companion.port} (接続情報: {companion.home / "connection.json"})', flush=True)
    stopping = threading.Event()
    def observe():
        while not stopping.wait(10):
            try: companion.poll_blm()
            except Exception: pass
    threading.Thread(target=observe,daemon=True).start()
    try:
        server.serve_forever(poll_interval=.5)
    finally:
        stopping.set()
        server.server_close()


def main():
    default_home = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'BoothUnityBridge'
    default_db = Path(os.environ.get('APPDATA', str(Path.home()))) / 'pm.booth.library-manager' / 'data.db'
    parser = argparse.ArgumentParser()
    parser.add_argument('--state-dir', type=Path, default=default_home)
    parser.add_argument('--database', type=Path, default=default_db)
    parser.add_argument('--downloads', type=Path, default=Path.home() / 'Downloads')
    parser.add_argument('--library-root', default='')
    parser.add_argument('--port', type=int, default=PORT)
    args = parser.parse_args()
    # Lock before touching connection/state files so a second launch is harmless.
    args.state_dir.mkdir(parents=True, exist_ok=True)
    instance_lock = (args.state_dir / 'instance.lock').open('a+b')
    instance_lock.seek(0)
    if os.name == 'nt':
        import msvcrt
        try: msvcrt.locking(instance_lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError: return
    serve(Companion(args.state_dir, args.database, args.downloads, args.library_root, args.port))


if __name__ == '__main__':
    main()
