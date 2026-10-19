"""Observe BLM-owned downloads. Never write to BLM's DB or item directories."""
from __future__ import annotations
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tarfile
import threading
import time
import zipfile

ACTIVE = ('Preparing', 'ReadyForBLM', 'AwaitingBLM', 'Verifying', 'NeedsAttention')
STABLE_SECONDS = 60
WAIT_SECONDS = 30 * 60


class BlmHandoff:
    def products(self):
        root, items = self.read_library()
        for product in items.values():
            product['registered'] = True
        for record in self.state['files'].values():
            items.setdefault(record['item_id'], dict(item_id=record['item_id'],
                name=record.get('item_name') or 'BOOTH #' + record['item_id'], directory='', registered=False))
        return root, items

    def next_job(self, background=True):
        with self.lock:
            for job in self.state['jobs']:
                if job['state'] == 'ReadyForBLM':
                    record = self.state['files'].get(job['key'])
                    if not record or not self.selected(record):
                        job.update(state='Cancelled', message='監視対象から外れました。'); self.save(); continue
                    # Recheck old preflight jobs too, including jobs created by 0.4.1.
                    if not record.get('remote_added'):
                        try:
                            _,items=self.read_library();product=items.get(record['item_id'])
                            found,_=self.existing_blm(Path(product['directory']),record) if product else ({},False)
                            if found:
                                for name in found: self.validate_blm_file(Path(product['directory'])/name)
                                self.remember_existing(job['id'],record,Path(product['directory']),found)
                                continue
                        except Exception:
                            self.attention_blm(job['id'],'既存ファイルを確認できません。再取得せず停止しました。')
                            return {'job':None}
                    job.update(state='AwaitingBLM', dispatched_at=time.time(), message='BLMの登録・保存を待っています。')
                    self.save()
                    return {'job': {**job, **job['file_record']}}
            if any(j['state'] in ACTIVE for j in self.state['jobs']):
                return {'job': None}
            candidate = None
            for job in self.state['jobs']:
                if job['state'] not in ('Queued', 'Retry') or job.get('retry_at', 0) > time.time(): continue
                record = self.state['files'].get(job['key'])
                if not record or not self.selected(record):
                    job['state'] = 'Cancelled'; continue
                job.update(state='Preparing', attempts=job['attempts']+1, file_record=copy.deepcopy(record),
                           message='BLMの既存保存物と取得の必要性を確認しています。')
                self.processing.add(job['id']); candidate=job['id']; self.save(); break
        if candidate:
            if background:
                threading.Thread(target=self.prepare_blm, args=(candidate,), daemon=True).start()
            else:
                self.prepare_blm(candidate)
                return self.next_job(background=False)
        return {'job': None}

    def prepare_blm(self, job_id):
        try:
            with self.lock:
                record=copy.deepcopy(self.job(job_id)['file_record'])
            _, items=self.read_library()
            product=items.get(record['item_id'])
            baseline,known_version=self.existing_blm(Path(product['directory']),record) if product else ({},False)
            if baseline and (not record.get('remote_added') or known_version):
                for name in baseline: self.validate_blm_file(Path(product['directory'])/name)
                self.remember_existing(job_id,record,Path(product['directory']),baseline)
                return
            self.backup_blm(record['item_id'], baseline, job_id)
            with self.lock:
                self.job(job_id).update(state='ReadyForBLM', baseline=baseline, message='既存ファイルの保管済み。BLMへ引き渡せます。')
                self.save()
        except Exception:
            self.fail(job_id, '取得前の保管に失敗しました。保存容量・BLM保存先を確認してください。')
        finally:
            with self.lock: self.processing.discard(job_id)

    @staticmethod
    def version_name(stem):
        match=re.search(r'(?i)v(?:er(?:sion)?)?[ ._-]*(\d+(?:[._]\d+)*)',stem)
        if not match: return None
        numbers=[int(x) for x in re.split(r'[._]',match[1])]
        while len(numbers)>1 and numbers[-1]==0: numbers.pop()
        return (stem[:match.start()]+'{version}'+stem[match.end():]).casefold(),tuple(numbers)

    def existing_blm(self,product,record):
        exact=self.scan_blm(product,record)
        remote=self.version_name(Path(record['name']).stem)
        if not remote: return exact,False
        if not product.is_dir(): return {},False
        candidates=[]
        for top in product.iterdir():
            stem=re.sub(r' \([1-9][0-9]*\)$','',top.name if top.is_dir() else top.stem)
            local=self.version_name(stem)
            if local and local[0]==remote[0] and local[1]>=remote[1]:
                candidates.append((local[1],stem+Path(record['name']).suffix if top.is_dir() else top.name))
        for _,name in sorted(candidates,reverse=True):
            found=self.scan_blm(product,{**record,'name':name})
            if found: return found,True
        return exact,bool(exact)

    def remember_existing(self,job_id,record,product,files):
        from server import utc
        # Use one complete native save, excluding BLM's duplicate-folder suffix.
        group=max(files,key=lambda k:files[k]['mtime_ns']).split('/')[0]
        chosen={k:v for k,v in files.items() if k.split('/')[0]==group}
        logical=[(k.split('/',1)[1] if '/' in k else record['name'],v['size'],v['sha256']) for k,v in chosen.items()]
        digest=hashlib.sha256(json.dumps(sorted(logical),ensure_ascii=False).encode()).hexdigest()
        receipt=dict(key=record['key'],item_id=record['item_id'],name=record['name'],sha256=digest,
            saved_at=utc(),target_path=str(product/group),transport='blm',digest_kind='file-tree',baseline=True,
            package_hashes=[v['sha256'] for k,v in chosen.items() if k.lower().endswith('.unitypackage')],files=chosen)
        with self.lock:
            if not any(r['key']==record['key'] and r['sha256']==digest for r in self.state['revisions']):
                self.state['revisions'].append(receipt)
            self.state['files'][record['key']].update(saved_name=record['name'],latest_sha256=digest,
                saved_at=receipt['saved_at'],verified_at=time.time(),remote_added=False,local_status='Existing')
            self.job(job_id).update(state='Existing',message='BLM保存済みのファイルを認識。再取得を省略しました。',ended_at=utc())
            self.save()

    @staticmethod
    def scan_blm(product, record):
        from server import inside, sha256, MAX_FILES, MAX_EXPANDED, MAX_FILE
        if not product.exists(): return {}
        if product.is_symlink() or not product.is_dir(): raise ValueError('Unsafe BLM directory')
        name=record['name']; stem=Path(name).stem
        result={}; size=0
        for top in product.iterdir():
            # Observed BLM ZIP layout: name/ and name (1)/. Other files may be copied as-is.
            matches=(re.fullmatch(re.escape(stem)+r'(?: \([1-9][0-9]*\))?',top.name,re.I) if top.is_dir() else
                     re.fullmatch(re.escape(stem)+r'(?: \([1-9][0-9]*\))?'+re.escape(Path(name).suffix),top.name,re.I))
            if not matches: continue
            paths=top.rglob('*') if top.is_dir() else [top]
            for file in paths:
                if not inside(file, product) or file.is_symlink() or (hasattr(file,'is_junction') and file.is_junction()):
                    raise ValueError('Unsafe BLM file')
                if file.is_dir(): continue
                before=file.stat(); size+=before.st_size
                if len(result)>=MAX_FILES or size>MAX_EXPANDED or before.st_size>MAX_FILE: raise ValueError('Snapshot too large')
                digest=sha256(file); after=file.stat()
                if (before.st_size,before.st_mtime_ns)!=(after.st_size,after.st_mtime_ns): raise ValueError('BLM is still writing')
                result[file.relative_to(product).as_posix()]=dict(sha256=digest,size=after.st_size,mtime_ns=after.st_mtime_ns)
        return result

    def backup_blm(self, item, entries, job_id):
        from server import sha256
        _, items=self.read_library()
        if not entries: return
        product=Path(items[item]['directory'])
        for name, entry in entries.items():
            target=self.home/'blm-objects'/item/entry['sha256']
            target.parent.mkdir(parents=True,exist_ok=True)
            if target.exists():
                if sha256(target)!=entry['sha256']: raise ValueError('Stored backup was modified')
                continue
            temp=target.with_name(target.name+'.'+job_id+'.part')
            try:
                shutil.copyfile(product/name,temp)
                if sha256(temp)!=entry['sha256']: raise ValueError('BLM changed during backup')
                os.replace(temp,target)
            finally: temp.unlink(missing_ok=True)

    @staticmethod
    def validate_blm_file(file):
        from server import MAX_EXPANDED, MAX_FILES
        if file.suffix.lower()=='.unitypackage':
            with tarfile.open(file,'r|gz') as tar:
                found=False; total=0; count=0
                for member in tar:
                    total+=member.size; count+=1
                    if total>MAX_EXPANDED or count>MAX_FILES: raise ValueError('Package too large')
                    if member.name.endswith('/pathname'): found=True
                if not found: raise ValueError('Invalid Unity package')
        elif file.suffix.lower()=='.zip':
            with zipfile.ZipFile(file) as archive:
                if len(archive.infolist())>MAX_FILES or sum(x.file_size for x in archive.infolist())>MAX_EXPANDED:
                    raise ValueError('ZIP too large')
                if archive.testzip() is not None: raise ValueError('Invalid ZIP')
        else:
            with file.open('rb') as stream: prefix=stream.read(512).lstrip().lower()
            if prefix.startswith((b'<html',b'<!doctype html')): raise ValueError('HTML response')

    def poll_blm(self, now=None):
        from server import utc, atomic_json
        now=time.time() if now is None else now
        # One observer; expensive hashing and archiving never blocks status HTTP.
        if not self.observer_lock.acquire(blocking=False): return
        try:
            with self.lock:
                jobs=[copy.deepcopy(j) for j in self.state['jobs'] if j['state'] in ('AwaitingBLM','Verifying','NeedsAttention')]
            for snapshot in jobs:
                job_id=snapshot['id']; record=snapshot['file_record']
                try:
                    root, items=self.read_library(); item=items.get(record['item_id'])
                    if not item or not Path(item['directory']).is_dir():
                        self.wait_blm(job_id,now); continue
                    product=Path(item['directory'])
                    from server import inside
                    if not inside(product,root): raise ValueError('BLM directory escaped library')
                    current=self.scan_blm(product,record)
                    changed={k:v for k,v in current.items() if k not in snapshot['baseline'] or v!=snapshot['baseline'][k]}
                    groups={k.split('/')[0] for k in changed}
                    if not changed: self.wait_blm(job_id,now); continue
                    if len(groups)!=1:
                        self.attention_blm(job_id,'複数の取得先が同時に変化しました。BLMで内容を確認してください。'); continue
                    group=next(iter(groups))
                    files={k:v for k,v in current.items() if k.split('/')[0]==group}
                    signature=hashlib.sha256(json.dumps(files,sort_keys=True).encode()).hexdigest()
                    with self.lock:
                        job=self.job(job_id)
                        if job.get('observed_signature')!=signature:
                            job.update(state='Verifying', observed_signature=signature, stable_since=now,
                                       message='BLMの保存ファイルを検出。内容が安定するまで確認中。'); self.save(); continue
                        if now-job['stable_since']<STABLE_SECONDS: continue
                    for name in files: self.validate_blm_file(product/name)
                    self.backup_blm(record['item_id'], files, job_id)
                    if self.scan_blm(product,record)!=current:
                        with self.lock:
                            self.job(job_id).pop('observed_signature',None); self.save()
                        continue
                    # Ignore BLM's duplicate-folder suffix when comparing file content.
                    logical=[(k.split('/',1)[1] if '/' in k else record['name'],v['size'],v['sha256']) for k,v in files.items()]
                    digest=hashlib.sha256(json.dumps(sorted(logical),ensure_ascii=False).encode()).hexdigest()
                    receipt=dict(key=record['key'], item_id=record['item_id'], name=record['name'], sha256=digest,
                        saved_at=utc(), target_path=str(product/group), transport='blm', digest_kind='file-tree',
                        package_hashes=[v['sha256'] for k,v in files.items() if k.lower().endswith('.unitypackage')], files=files)
                    atomic_json(self.home/'blm-receipts'/record['item_id']/(job_id+'.json'),receipt)
                    with self.lock:
                        unchanged=any(r['key']==record['key'] and r['sha256']==digest for r in self.state['revisions'])
                        if not unchanged: self.state['revisions'].append(receipt)
                        self.state['files'][record['key']].update(verified_at=time.time(),saved_name=record['name'],
                            latest_sha256=digest,saved_at=receipt['saved_at'],remote_added=False,local_status='Saved')
                        self.job(job_id).update(state='Unchanged' if unchanged else 'Saved',
                            message='BLMの保存内容を確認（同一内容）' if unchanged else 'BLM登録・保存内容を確認済み',ended_at=utc())
                        self.save()
                except Exception:
                    # A partial file may not parse until the next observation. Do not report success or redispatch.
                    with self.lock:
                        job=self.job(job_id); job.pop('observed_signature',None)
                        self.wait_blm(job_id,now)
        finally: self.observer_lock.release()

    def attention_blm(self,job_id,message):
        with self.lock:
            self.job(job_id).update(state='NeedsAttention',message=message); self.save()

    def wait_blm(self,job_id,now):
        with self.lock:
            if now-self.job(job_id).get('dispatched_at',now)>=WAIT_SECONDS:
                self.attention_blm(job_id,'BLMの保存を確認できません。BLM起動・Chromeの確認画面・認証を確認してください。自動再送は停止しています。')

    def retry_blm(self,job_id):
        with self.lock:
            job=self.job(job_id)
            if job['state']!='NeedsAttention': raise ValueError('確認が必要なジョブだけを再試行できます。')
            import uuid
            from server import utc
            if job.get('source')!='manual_missing': raise ValueError('旧自動取得は再試行できません。')
            record=self.state['files'].get(job['key'])
            if not record or not record.get('available') or record['name']!=job.get('requested_name'):
                raise ValueError('配布ファイルが変わりました。一括取得から選び直してください。')
            job.update(state='Cancelled',message='利用者が再試行を要求しました。')
            self.state['jobs'].append(dict(id=uuid.uuid4().hex,key=job['key'],state='Queued',created_at=utc(),attempts=0,
                transport='blm',source='manual_missing',batch_id=job['batch_id'],requested_name=job['requested_name']))
            self.save()
        return {'ok':True}

    def dismiss_blm(self,job_id):
        with self.lock:
            job=self.job(job_id)
            if job['state']!='NeedsAttention': raise ValueError('確認待ちの取得だけを停止できます。')
            item=job['file_record']['item_id']
            for pending in self.state['jobs']:
                if pending.get('batch_id')==job.get('batch_id') and pending['key'].split(':')[0]==item and pending['state'] in ('Queued','Retry','NeedsAttention'):
                    pending.update(state='Cancelled',message='利用者がこの商品の取得をスキップしました。')
            self.save()
        return {'ok':True}
