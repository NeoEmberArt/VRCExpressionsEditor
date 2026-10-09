import json, pathlib, sys, urllib.request

SESSION = 'd2949a3c2b874ac1b7413927a344c9f8'
def call(name, arguments):
    request = urllib.request.Request('http://127.0.0.1:8080/mcp', data=json.dumps({'jsonrpc':'2.0','id':1,'method':'tools/call','params':{'name':name,'arguments':arguments}}).encode(), headers={'Content-Type':'application/json','Accept':'application/json, text/event-stream','Mcp-Session-Id':SESSION})
    raw = urllib.request.urlopen(request, timeout=180).read().decode()
    payload = next(json.loads(line[6:]) for line in raw.splitlines() if line.startswith('data: '))
    return payload
if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    args = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf-8')) if len(sys.argv)>2 else {}
    result = call(sys.argv[1], args)
    pathlib.Path(__file__).with_name('last-response.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False))
