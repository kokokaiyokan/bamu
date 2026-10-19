"""Serve only a public HTML fixture and the product parser for browser QA."""
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
root=Path(__file__).resolve().parents[1]
page=b'''<!doctype html><meta charset="utf-8"><title>Product metadata parser check</title>
<pre id="result">Running...</pre><script type="module">
import {parseMetadata} from './metadata.js';
try {
 const html=await (await fetch('./sample.html')).text();
 const product=parseMetadata(new DOMParser().parseFromString(html,'text/html'),'https://booth.pm/ja/items/4739922','4739922');
 document.getElementById('result').textContent=JSON.stringify({ok:product.vrchat_state==='yes',...product},null,2);
} catch(error) {document.getElementById('result').textContent=JSON.stringify({ok:false,error:error.message});}
</script>'''
class Handler(BaseHTTPRequestHandler):
    def log_message(self,*_): pass
    def do_GET(self):
        if self.path=='/': body,kind=page,'text/html'
        elif self.path=='/metadata.js': body,kind=(root/'browser-extension/metadata.js').read_bytes(),'text/javascript'
        elif self.path=='/sample.html': body,kind=(root.parent/'bamu_development/artifacts/research/metadata-4739922.html').read_bytes(),'text/html'
        else: self.send_error(404);return
        self.send_response(200);self.send_header('Content-Type',kind+'; charset=utf-8');self.end_headers();self.wfile.write(body)
HTTPServer(('127.0.0.1',48765),Handler).serve_forever()
