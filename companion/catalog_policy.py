"""Product-level catalog, classification and automatic file selection policy."""
import re
import time
from pathlib import Path


class CatalogPolicy:
    def target_mode(self,item):
        return self.state['watches'].get(item,{}).get('target_mode','auto')

    def is_target(self,item):
        mode=self.target_mode(item)
        return mode=='include' or (mode=='auto' and self.state['metadata'].get(item,{}).get('vrchat_state')=='yes')

    def recommended(self,item,all_files=False):
        groups={}
        for f in self.state['files'].values():
            if f['item_id']!=item or not f.get('available'): continue
            suffix=Path(f['name']).suffix.lower()
            if not all_files:
                if suffix not in ('.zip','.unitypackage','.rar','.7z','.fbx','.png','.jpg','.jpeg','.tga'): continue
                if re.search(r'(?i)psd|(?:^|[ _.-])(source|sources|readme)(?:[ _.-]|$)',f['name']): continue
            version=self.version_name(Path(f['name']).stem)
            family=version[0] if version else Path(f['name']).stem.casefold()
            key=(f['variation_id'],family,suffix)
            rank=(version[1] if version else (),int(f['file_id']))
            if key not in groups or rank>groups[key][0]: groups[key]=(rank,f['key'])
        return {entry[1] for entry in groups.values()}

    def selected(self,record):
        # Only the exact file snapshot explicitly queued by a user can dispatch.
        return bool(record.get('available')) and any(
            j.get('source')=='manual_missing' and j['key']==record['key'] and
            j.get('requested_name')==record['name'] and
            j['state'] in ('Queued','Retry','Preparing','ReadyForBLM','AwaitingBLM','Verifying','NeedsAttention')
            for j in self.state['jobs'])

    def refresh_manual_choices(self):
        pass  # No automatic version following.

    def bulk_missing(self):
        from server import utc
        import uuid
        with self.lock:
            _,registered=self.read_library()
            batch=uuid.uuid4().hex
            added=[]
            for record in self.state['files'].values():
                item=record['item_id'];watch=self.state['watches'].get(item,{})
                if item in registered or not record.get('available') or not self.is_target(item): continue
                if watch.get('enabled') is False: continue
                choices=watch.get('file_keys',[]) if watch.get('selection_mode')=='manual' else self.recommended(item)
                if record['key'] not in choices: continue
                if any(j['key']==record['key'] and j['state'] in
                    ('Queued','Retry','Preparing','ReadyForBLM','AwaitingBLM','Verifying','NeedsAttention') for j in self.state['jobs']): continue
                job=dict(id=uuid.uuid4().hex,key=record['key'],state='Queued',created_at=utc(),attempts=0,
                    transport='blm',source='manual_missing',batch_id=batch,requested_name=record['name'])
                self.state['jobs'].append(job);added.append(item)
            self.save()
            return dict(ok=True,queued_products=len(set(added)),queued_files=len(added))

    def products(self):
        root,items=super().products()
        for item,product in items.items():
            meta=self.state['metadata'].get(item,{})
            if not product.get('registered') and meta.get('name'): product['name']=meta['name']
            product.update(shop=meta.get('shop',''),thumbnail_url=meta.get('thumbnail_url',''),
                tags=meta.get('tags',[]),category=meta.get('category',''),vrchat_state=meta.get('vrchat_state','unknown'),
                target_mode=self.target_mode(item),is_target=self.is_target(item),recommended_file_keys=sorted(self.recommended(item)))
            files=[f for f in self.state['files'].values() if f['item_id']==item and f.get('available')]
            product['remote_available']=bool(files)
            jobs=[j for j in self.state['jobs'] if self.state['files'].get(j['key'],{}).get('item_id')==item]
            if any(j['state']=='NeedsAttention' for j in jobs): state='NeedsAttention'
            elif any(j['state'] in ('Preparing','ReadyForBLM','AwaitingBLM','Verifying') for j in jobs): state='Processing'
            elif any(j['state']=='Queued' for j in jobs): state='Queued'
            elif not product.get('registered'): state='RemoteOnly'
            else: state='Saved'
            product['sync_state']=state
        return root,items

    def update_metadata(self,records):
        from server import utc
        from urllib.parse import urlsplit
        known={f['item_id'] for f in self.state['files'].values()}
        prepared={}
        for record in records:
            item=str(record.get('item_id',''))
            if item not in known: raise ValueError('BOOTHのライブラリにない商品です。')
            if record.get('failed'):
                prepared[item]={**self.state['metadata'].get(item,{}),'retry_after':time.time()+3600}
                continue
            image=str(record.get('thumbnail_url',''))
            if image and (urlsplit(image).scheme!='https' or urlsplit(image).netloc!='booth.pximg.net'): image=''
            classification=record.get('vrchat_state','unknown')
            if classification not in ('yes','no','unknown'): raise ValueError('分類が不正です。')
            prepared[item]=dict(item_id=item,name=str(record.get('name',''))[:300],shop=str(record.get('shop',''))[:200],
                thumbnail_url=image,tags=[str(x)[:100] for x in record.get('tags',[])[:1000]],category=str(record.get('category',''))[:200],
                tag_source_version=1 if record.get('tag_source_version')==1 else 0,
                vrchat_state=classification,fetched_at=time.time(),checked_at=utc())
        with self.lock:
            self.state['metadata'].update(prepared);self.queue_due();self.save()
        return {'ok':True}

    def metadata_requests(self):
        now=time.time();items=sorted({f['item_id'] for f in self.state['files'].values() if f.get('available')})
        return [item for item in items if self.state['metadata'].get(item,{}).get('retry_after',0)<=now and
                now-self.state['metadata'].get(item,{}).get('fetched_at',0)>
                (7*86400 if self.state['metadata'].get(item,{}).get('tag_source_version')==1 else 3600)][:3]
