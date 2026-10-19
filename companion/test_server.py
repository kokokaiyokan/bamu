from contextlib import closing
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import sqlite3
import sys
import tarfile
import tempfile
import threading
import time
import unittest
import uuid
from unittest.mock import patch
import zipfile
sys.path.insert(0,str(Path(__file__).parent))
from server import Companion, make_server, valid_file, safe_name, sha256


def package(path, value=b"asset"):
    path.parent.mkdir(parents=True,exist_ok=True)
    with tarfile.open(path,"w:gz") as tar:
        for name,data in [("a/pathname",b"Assets/Test.txt"),("a/asset",value)]:
            info=tarfile.TarInfo(name);info.size=len(data);tar.addfile(info,io.BytesIO(data))


class BlmTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
        self.library=self.root/"BLM";self.product=self.library/"b123";self.product.mkdir(parents=True)
        self.db=self.root/"data.db"
        with closing(sqlite3.connect(self.db)) as c,c:
            c.executescript("""CREATE TABLE schema_version(version INTEGER);INSERT INTO schema_version VALUES(20261005);
                CREATE TABLE preferences(item_directory_path BLOB);
                CREATE TABLE registered_items(id TEXT,booth_item_id INTEGER);
                CREATE TABLE booth_items(id INTEGER,name TEXT);
                CREATE TABLE overwritten_booth_items(booth_item_id INTEGER,name TEXT);
                INSERT INTO registered_items VALUES('b123',123);INSERT INTO booth_items VALUES(123,'Test');""")
            c.execute("INSERT INTO preferences VALUES(?)",(str(self.library).encode("utf-16-le"),))
        self.db_hash=sha256(self.db)
        self.app=Companion(self.root/"state",self.db,self.root/"Downloads",port=0)
        self.app.state['auto_enabled']=True
        self.record=dict(item_id="123",file_id="789",variation_id="456",name="asset.zip",item_name="Test")
        self.key="123:456:789"

    def tearDown(self):
        self.assertEqual(self.db_hash,sha256(self.db),"Companion must not write BLM DB")
        self.temp.cleanup()

    def watch(self, record=None, include_new=False):
        record=record or self.record
        self.app.catalog([record]);key=valid_file(record)['key']
        self.app.configure(dict(item_id=record['item_id'],enabled=True,file_keys=[key],include_new_files=include_new))
        self.app.catalog([record])
        # Native observer tests seed an explicit request (including race scenarios
        # where BLM registered a product after the bulk snapshot was captured).
        self.enqueue(record)
        return self.app.next_job(background=False)['job']

    def enqueue(self,record):
        self.app.state['jobs'].append(dict(id=uuid.uuid4().hex,key=valid_file(record)['key'],state='Queued',
            attempts=0,transport='blm',source='manual_missing',batch_id='test-batch',requested_name=record['name']))

    def finish(self,job,folder="asset",value=b"data"):
        path=self.product/folder/"nested"/"test.unitypackage";package(path,value)
        self.app.poll_blm(now=time.time())
        self.assertEqual('Verifying',self.app.job(job['id'])['state'])
        self.app.poll_blm(now=time.time()+61)
        return path

    def watch_updated(self):
        self.app.catalog([self.record])
        self.record={**self.record,'file_id':'790'};self.key=valid_file(self.record)['key']
        self.app.catalog([self.record])
        return self.watch()

    def test_no_download_without_selection(self):
        self.app.catalog([self.record]);self.assertIsNone(self.app.next_job(False)['job'])

    def classify(self,state='yes'):
        self.app.update_metadata([dict(item_id='123',name='VRChat Asset',vrchat_state=state,tags=['VRChat'] if state=='yes' else [])])

    def test_catalog_and_metadata_never_start_downloads(self):
        self.app.catalog([self.record]);self.classify()
        self.assertIsNone(self.app.next_job(False)['job'])
        self.assertEqual([],self.app.state['jobs'])

    def test_unclassified_and_non_vrchat_not_auto_downloaded(self):
        self.app.catalog([self.record]);self.assertIsNone(self.app.next_job(False)['job'])
        self.classify('no');self.assertIsNone(self.app.next_job(False)['job'])

    def test_legacy_enabled_watch_never_queues_work(self):
        self.app.catalog([self.record]);self.classify()
        self.app.configure(dict(item_id='123',enabled=True,file_keys=[self.key],include_new_files=True))
        self.app.state['auto_enabled']=True;self.app.queue_due()
        self.assertEqual([],self.app.state['jobs'])

    def test_auto_candidates_select_latest_and_exclude_source_psd(self):
        records=[{**self.record,'name':'Asset_v1.0.zip'}, {**self.record,'file_id':'790','name':'Asset_v2.0.zip'},
                 {**self.record,'file_id':'791','name':'Asset_PSD.zip'}]
        self.app.catalog(records);self.classify()
        self.assertEqual({'123:456:790'},self.app.recommended('123'))
        self.assertIsNone(self.app.next_job(False)['job'])

    def test_product_exclusion_overrides_positive_tag(self):
        self.app.catalog([self.record]);self.classify()
        self.app.configure(dict(item_id='123',enabled=True,file_keys=[self.key],target_mode='exclude'))
        self.assertIsNone(self.app.next_job(False)['job'])

    def test_manual_override_allows_unknown_product(self):
        self.assertIsNotNone(self.watch())

    def test_new_version_does_not_follow_manual_file_choices(self):
        self.record={**self.record,'name':'Asset_v1.0.zip'}
        self.watch();self.app.state['jobs'][0]['state']='Saved'
        self.app.catalog([self.record,{**self.record,'name':'Asset_v2.0.zip','file_id':'790'}])
        self.assertEqual([self.key],self.app.state['watches']['123']['file_keys'])
        self.assertIsNone(self.app.next_job(False)['job'])

    def test_metadata_products_remain_grouped_by_item(self):
        self.app.catalog([self.record,{**self.record,'file_id':'790'},{**self.record,'file_id':'791','variation_id':'999'}]);self.classify()
        product=self.app.status()['products']
        self.assertEqual(1,len(product));self.assertEqual('123',product[0]['item_id']);self.assertTrue(product[0]['is_target'])

    def test_metadata_rejects_unowned_items_and_untrusted_thumbnail(self):
        self.app.catalog([self.record])
        with self.assertRaises(ValueError): self.app.update_metadata([dict(item_id='999',vrchat_state='yes')])
        self.app.update_metadata([dict(item_id='123',vrchat_state='yes',thumbnail_url='https://evil.example/image')])
        self.assertEqual('',self.app.state['metadata']['123']['thumbnail_url'])

    def test_json_metadata_preserves_tags_beyond_the_first_hundred(self):
        self.app.catalog([self.record])
        values=['Avatar'+str(i) for i in range(110)]+['hair']
        self.app.update_metadata([dict(item_id='123',tags=values,tag_source_version=1)])
        self.assertEqual(values,self.app.status()['products'][0]['tags'])
        self.assertNotIn('123',self.app.metadata_requests())
        self.assertEqual([],self.app.state['jobs'])

    def test_upgrade_refreshes_legacy_tags_once_with_fallback_backoff(self):
        self.app.catalog([self.record]);self.classify()
        self.app.state.pop('tag_parser_version',None);self.app.save()
        self.app=Companion(self.app.home,self.db,self.root/'Downloads',port=0)
        self.assertIn('123',self.app.metadata_requests())
        self.classify()  # HTML fallback is cached for an hour, not retried each tick.
        self.assertNotIn('123',self.app.metadata_requests())

    def test_failed_job_can_disable_only_its_product_and_continue(self):
        job=self.watch();self.app.fail(job['id'])
        self.app.dismiss_blm(job['id'])
        self.assertEqual('Cancelled',self.app.job(job['id'])['state'])
        self.assertTrue(self.app.state['watches']['123']['enabled'])

    def test_registered_products_do_not_offer_remote_update_acquisition(self):
        self.app.catalog([self.record]);self.app.state['files'][self.key]['saved_name']='old-name.zip'
        self.app.state['auto_enabled']=False
        self.assertEqual('Saved',self.app.status()['products'][0]['sync_state'])

    def test_unregistered_purchased_product_can_be_selected(self):
        record={**self.record,'item_id':'999'};job=self.watch(record)
        self.assertEqual('999',job['item_id']);self.assertEqual({},job['baseline'])
        self.assertFalse(next(p for p in self.app.status()['products'] if p['item_id']=='999')['registered'])
        # Simulate BLM registering the new item itself after opening the link.
        with closing(sqlite3.connect(self.db)) as c,c:
            c.execute("INSERT INTO registered_items VALUES('b999',999)")
            c.execute("INSERT INTO booth_items VALUES(999,'New')")
        self.db_hash=sha256(self.db);self.product=self.library/'b999';self.product.mkdir()
        self.finish(job)
        self.assertEqual('Saved',self.app.job(job['id'])['state'])

    def test_success_requires_registered_blm_files_and_stability(self):
        job=self.watch();self.app.poll_blm();self.assertEqual('AwaitingBLM',self.app.job(job['id'])['state'])
        file=self.finish(job)
        self.assertEqual('Saved',self.app.job(job['id'])['state'])
        self.assertEqual(sha256(file),self.app.state['revisions'][0]['package_hashes'][0])
        self.assertTrue((self.app.home/'blm-objects'/'123'/sha256(file)).exists())

    def test_existing_files_adopted_without_download(self):
        package(self.product/'asset'/'test.unitypackage')
        self.assertIsNone(self.watch())
        self.assertEqual('Existing',self.app.state['jobs'][0]['state'])
        self.assertEqual('asset.zip',self.app.state['files'][self.key]['saved_name'])
        self.app.catalog([self.record])
        self.assertIsNone(self.app.next_job(False)['job'])

    def test_existing_newer_version_prevents_older_download(self):
        package(self.product/'asset_v2.0'/'test.unitypackage')
        self.assertIsNone(self.watch({**self.record,'name':'asset_v1.0.zip'}))
        self.assertEqual('Existing',self.app.state['jobs'][0]['state'])

    def test_existing_older_version_allows_newer_download(self):
        package(self.product/'asset_v1.0'/'test.unitypackage')
        self.assertIsNotNone(self.watch({**self.record,'name':'asset_v2.0.zip'}))

    def test_new_file_id_with_same_explicit_version_not_downloaded(self):
        self.record={**self.record,'name':'asset_v1.0.zip'}
        package(self.product/'asset_v1.0'/'test.unitypackage')
        self.assertIsNone(self.watch_updated())

    def test_old_ready_job_rechecked_after_upgrade(self):
        job=self.watch();self.app.job(job['id'])['state']='ReadyForBLM'
        package(self.product/'asset'/'test.unitypackage')
        self.app.job(job['id'])['baseline']=self.app.scan_blm(self.product,self.record)
        self.assertIsNone(self.app.next_job(False)['job'])
        self.assertEqual('Existing',self.app.job(job['id'])['state'])

    def test_enabled_old_periodic_setting_cannot_force_download(self):
        job=self.watch();self.finish(job)
        self.app.state['verify_hours']=1;self.app.state['files'][self.key]['verified_at']=0
        self.app.catalog([self.record]);self.assertIsNone(self.app.next_job(False)['job'])

    def test_preexisting_files_backed_up_before_dispatch(self):
        file=self.product/'asset'/'test.unitypackage';package(file,b'old');old=sha256(file)
        job=self.watch_updated()
        self.assertTrue((self.app.home/'blm-objects'/'123'/old).exists())
        package(file,b'new');self.app.poll_blm();self.app.poll_blm(now=time.time()+61)
        self.assertEqual('Saved',self.app.job(job['id'])['state'])
        self.assertEqual(old,sha256(self.app.home/'blm-objects'/'123'/old))

    def test_native_duplicate_folder_same_content_not_new_revision(self):
        job=self.watch();self.finish(job)
        # Copy exact package bytes because gzip headers may include current time.
        original=self.product/'asset'/'nested'/'test.unitypackage'
        self.record={**self.record,'file_id':'790'}
        self.app.configure(dict(item_id='123',enabled=True,file_keys=[self.key],include_new_files=True))
        self.app.catalog([self.record]);self.enqueue(self.record);job2=self.app.next_job(False)['job']
        copy=self.product/'asset (1)'/'nested'/'test.unitypackage';copy.parent.mkdir(parents=True);copy.write_bytes(original.read_bytes())
        self.app.poll_blm();self.app.poll_blm(now=time.time()+61)
        self.assertEqual('Saved',self.app.job(job2['id'])['state'])
        self.assertEqual(self.app.state['revisions'][0]['sha256'],self.app.state['revisions'][1]['sha256'])

    def test_partial_package_never_success(self):
        job=self.watch();file=self.product/'asset'/'test.unitypackage';file.parent.mkdir();file.write_bytes(b'partial')
        self.app.poll_blm();self.app.poll_blm(now=time.time()+61)
        self.assertNotIn(self.app.job(job['id'])['state'],['Saved','Unchanged'])
        self.assertEqual([],self.app.state['revisions'])

    def test_unrelated_file_not_attributed(self):
        job=self.watch();package(self.product/'unrelated'/'test.unitypackage')
        self.app.poll_blm();self.assertEqual('AwaitingBLM',self.app.job(job['id'])['state'])

    def test_concurrent_same_file_downloads_require_review(self):
        job=self.watch();package(self.product/'asset'/'test.unitypackage');package(self.product/'asset (1)'/'test.unitypackage')
        self.app.poll_blm();self.assertEqual('NeedsAttention',self.app.job(job['id'])['state'])

    def test_restart_observes_dispatched_job_without_redispatch(self):
        job=self.watch();self.app=Companion(self.root/'state',self.db,self.root/'Downloads',port=0)
        self.assertIsNone(self.app.next_job(False)['job']);self.finish(job)
        self.assertEqual('Saved',self.app.job(job['id'])['state'])

    def test_dispatch_failure_waits_for_explicit_retry(self):
        job=self.watch();self.app.fail(job['id']);self.assertEqual('NeedsAttention',self.app.job(job['id'])['state'])
        self.assertIsNone(self.app.next_job(False)['job'])
        self.app.retry_blm(job['id']);self.assertIsNotNone(self.app.next_job(False)['job'])

    def test_default_does_not_repeatedly_download_identical_named_file(self):
        job=self.watch();self.finish(job)
        self.app.state['files'][self.key]['verified_at']=0
        self.app.catalog([self.record]);self.assertIsNone(self.app.next_job(False)['job'])
        self.assertEqual(0,self.app.state['verify_hours'])

    def test_backup_failure_prevents_external_app_dispatch(self):
        with patch.object(self.app,'backup_blm',side_effect=OSError('disk full')):
            job=self.watch()
        self.assertIsNone(job)
        self.assertEqual('Retry',self.app.state['jobs'][0]['state'])

    def test_mutated_backup_is_not_overwritten_or_dispatched(self):
        file=self.product/'asset'/'test.unitypackage';package(file)
        digest=sha256(file);target=self.app.home/'blm-objects'/'123'/digest
        target.parent.mkdir(parents=True);target.write_bytes(b'modified')
        self.assertIsNone(self.watch_updated())
        self.assertEqual(b'modified',target.read_bytes())

    def test_deselected_queued_job_cancelled(self):
        self.app.catalog([self.record]);self.app.configure(dict(item_id='123',enabled=True,file_keys=[self.key]));self.app.catalog([self.record])
        self.app.configure(dict(item_id='123',enabled=True,file_keys=[]));self.assertIsNone(self.app.next_job(False)['job'])

    def test_new_file_never_extends_a_manual_batch(self):
        job=self.watch(include_new=True);self.app.job(job['id'])['state']='Saved'
        self.app.catalog([self.record,{**self.record,'file_id':'790','name':'new.zip'}])
        self.assertIsNone(self.app.next_job(False)['job'])
        self.assertEqual(1,len(self.app.state['jobs']))

    def test_partial_catalog_preserves_availability(self):
        self.watch();self.app.catalog([],False);self.assertTrue(self.app.state['files'][self.key]['available'])

    def test_handoff_url_is_official_and_input_validated(self):
        self.assertEqual('https://booth.pm/downloadables/789/deeplink?client=booth-library-manager&variation_id=456',
            valid_file({**self.record,'url':'https://evil.example'})['url'])
        for name in ['../a','a:b','NUL.txt','file.']:
            with self.assertRaises(ValueError):safe_name(name)

    def test_unsafe_zip_still_rejected(self):
        archive=self.root/'bad.zip';stage=self.root/'stage';stage.mkdir()
        with zipfile.ZipFile(archive,'w') as z:z.writestr('../outside','data')
        with self.assertRaises(ValueError):self.app.unpack(archive,stage)
        self.assertFalse((self.root/'outside').exists())

    def missing(self):
        record={**self.record,'item_id':'999'}
        self.app.catalog([self.record,record])
        self.app.update_metadata([dict(item_id='999',vrchat_state='yes')])
        return record

    def test_bulk_only_unregistered_products_even_with_remote_updates(self):
        record=self.missing();self.classify()
        self.app.state['files'][self.key]['remote_added']=True
        result=self.app.bulk_missing()
        self.assertEqual((1,1),(result['queued_products'],result['queued_files']))
        self.assertEqual('999',self.app.next_job(False)['job']['item_id'])

    def test_bulk_repeated_click_does_not_duplicate_pending_files(self):
        self.missing();self.app.bulk_missing()
        self.assertEqual(0,self.app.bulk_missing()['queued_files'])
        self.assertEqual(1,len(self.app.state['jobs']))

    def test_bulk_unknown_skipped_until_manual_include(self):
        record={**self.record,'item_id':'999'};self.app.catalog([record])
        self.assertEqual(0,self.app.bulk_missing()['queued_files'])
        self.app.configure(dict(item_id='999',enabled=True,target_mode='include',file_keys=[valid_file(record)['key']]))
        self.assertEqual(1,self.app.bulk_missing()['queued_files'])

    def test_bulk_name_changed_after_request_is_not_dispatched(self):
        record=self.missing();self.app.bulk_missing()
        self.app.catalog([{**record,'name':'replacement.zip'}])
        self.assertIsNone(self.app.next_job(False)['job'])
        self.assertEqual('Cancelled',self.app.state['jobs'][0]['state'])

    def test_bulk_multiple_files_continue_after_first_blm_registration(self):
        record=self.missing();other={**record,'file_id':'790','name':'extra.zip'}
        self.app.catalog([record,other]);result=self.app.bulk_missing()
        self.assertEqual((1,2),(result['queued_products'],result['queued_files']))
        job=self.app.next_job(False)['job']
        with closing(sqlite3.connect(self.db)) as c,c:
            c.execute("INSERT INTO registered_items VALUES('b999',999)")
            c.execute("INSERT INTO booth_items VALUES(999,'New')")
        self.db_hash=sha256(self.db);self.product=self.library/'b999';self.product.mkdir()
        self.finish(job)
        self.assertEqual('790',self.app.next_job(False)['job']['file_id'])

    def test_legacy_pending_work_cancelled_on_upgrade(self):
        self.app.state['jobs']=[dict(id='legacy',key=self.key,state='Queued',transport='blm',attempts=0)]
        self.app.state['auto_enabled']=True;self.app.save()
        restored=Companion(self.app.home,self.db,self.root/'Downloads',port=0)
        self.assertFalse(restored.state['auto_enabled'])
        self.assertEqual('Cancelled',restored.state['jobs'][0]['state'])
        self.assertIsNone(restored.next_job(False)['job'])

    def test_bulk_explicit_queue_survives_restart_without_future_auto_work(self):
        self.missing();self.app.bulk_missing()
        self.app=Companion(self.app.home,self.db,self.root/'Downloads',port=0)
        self.assertEqual('999',self.app.next_job(False)['job']['item_id'])

    def test_http_auth_and_download_api_removed(self):
        server=make_server(self.app);self.app.port=server.server_port
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        def request(headers,path='/v1/status',method='GET',body=None):
            c=http.client.HTTPConnection('127.0.0.1',server.server_port,timeout=3)
            c.request(method,path,body=body,headers=headers);r=c.getresponse();data=r.read();c.close();return r.status
        try:
            headers={'Authorization':'Bearer '+self.app.token}
            self.assertEqual(403,request({}));self.assertEqual(200,request(headers))
            self.assertEqual(403,request({**headers,'Origin':'https://evil.example'}))
            self.assertEqual(403,request({**headers,'Host':'evil.example'}))
            self.assertEqual(200,request({**headers,'Origin':'chrome-extension://'+'a'*32}))
            self.assertEqual(404,request(headers,'/v1/next'))
            self.assertEqual(200,request(headers,'/v1/next','POST','{}'))
            self.assertEqual(404,request(headers,'/v1/downloaded','POST','{}'))
            self.assertEqual(200,request(headers,'/v1/bulk-missing','POST','{}'))
        finally:server.shutdown();server.server_close();thread.join(timeout=3)

if __name__=='__main__':unittest.main()
