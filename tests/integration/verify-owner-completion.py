# Run from the repository root after building OwnerHarness.csproj in Release.
# Requires the separately installed official Kotlin LSP and existing sample JDK.
# Launches a real Gradle/JVM debuggee; never calls a suggested function.
from pathlib import Path
Path("artifacts").mkdir(exist_ok=True)
import json, pathlib, os, queue, socket, subprocess, threading, time
root=pathlib.Path('samples/kotlin-jvm').resolve()
server=pathlib.Path(os.environ['LOCALAPPDATA'])/'Vistudio/servers/kotlin-lsp-263.7084.0/bin/intellij-server.exe'
log=open('artifacts/owner-frames-probe.jsonl','w',encoding='utf8')
lock=threading.Lock()
class Peer:
 def __init__(self,reader,writer,name):
  self.reader,self.writer,self.name=reader,writer,name
  self.q=queue.Queue(); self.seq=0; self.pending=[]
  threading.Thread(target=self.read,daemon=True).start()
 def read(self):
  try:
   while True:
    headers={}
    while True:
     line=self.reader.readline()
     if not line: raise EOFError()
     if line==b'\r\n': break
     k,v=line.decode().split(':',1); headers[k.lower()]=v.strip()
    msg=json.loads(self.reader.read(int(headers['content-length'])))
    with lock: log.write(json.dumps({'peer':self.name,'direction':'in','message':msg})+'\n'); log.flush()
    if self.name=='lsp' and 'method' in msg and 'id' in msg:
     method=msg['method']; result=None
     if method=='workspace/configuration': result=[None for _ in msg['params']['items']]
     self.send({'jsonrpc':'2.0','id':msg['id'],'result':result})
    else: self.q.put(msg)
  except Exception as e: self.q.put({'read_error':str(e)})
 def send(self,msg):
  data=json.dumps(msg).encode()
  with lock:
   log.write(json.dumps({'peer':self.name,'direction':'out','message':msg})+'\n'); log.flush()
   self.writer.write(b'Content-Length: '+str(len(data)).encode()+b'\r\n\r\n'+data); self.writer.flush()
 def request(self,method,args):
  self.seq+=1
  self.send({'jsonrpc':'2.0','id':self.seq,'method':method,'params':args} if self.name=='lsp' else {'seq':self.seq,'type':'request','command':method,'arguments':args})
  return self.seq
 def wait(self,predicate,timeout=120):
  end=time.monotonic()+timeout
  while True:
   for i,m in enumerate(self.pending):
    if predicate(m): return self.pending.pop(i)
   m=self.q.get(timeout=max(0.1,end-time.monotonic()))
   if 'read_error' in m: raise RuntimeError(m)
   self.pending.append(m)
 def response(self,seq,timeout=120):
  result=self.wait(lambda m:m.get('id' if self.name=='lsp' else 'request_seq')==seq,timeout)
  print(json.dumps(result)[:2000],flush=True)
  if 'error' in result or result.get('success')==False: raise RuntimeError(result)
  return result.get('result') if self.name=='lsp' else result.get('body',{})
 def call(self,method,args): return self.response(self.request(method,args))
env=os.environ.copy(); env['IJ_JAVA_OPTIONS']='-Djavax.net.ssl.trustStoreType=Windows-ROOT -Djavax.net.ssl.trustStore=NUL'
process=subprocess.Popen([str(server),'--stdio','--system-path='+os.environ['LOCALAPPDATA']+'/Vistudio/kotlin-lsp-owner-probe'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=open('artifacts/owner-frames-probe.stderr','w'),env=env)
def complete(frame, chain, expected):
 start=time.monotonic()
 harness=subprocess.Popen([str(pathlib.Path('tests/integration/OwnerHarness/bin/Release/net472/OwnerHarness.exe').resolve()),str(file),str(frame['line']),chain],stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True,encoding='utf8')
 try:
  while True:
   line=harness.stdout.readline()
   if not line: raise RuntimeError('completion harness ended before result')
   message=json.loads(line)
   if message['kind']=='result':
    items=message['items']; names=[x['Name'] for x in items]
    assert expected in names,(frame,chain,items)
    assert 'ownerWrong' not in names,(frame,chain,items)
    assert all(not x.startswith('ownerInner' if 'OuterOwner' in frame['name'] else 'ownerOuter') for x in names),items
    assert all(x['Insertion']==x['Name'] for x in items),items
    print('VERIFIED',frame['name'],frame['line'],chain,json.dumps(items),'seconds',round(time.monotonic()-start,3),flush=True)
    harness.wait(timeout=5)
    return
   if message['kind']=='request': result=lsp.call(message['method'],message['arguments'])
   else:
    lsp.send({'jsonrpc':'2.0','method':message['method'],'params':message['arguments']}); result=None
   harness.stdin.write(json.dumps(result)+'\n'); harness.stdin.flush()
 finally:
  if harness.poll() is None: harness.kill()

try:
 lsp=Peer(process.stdout,process.stdin,'lsp')
 lsp.call('initialize',{'processId':os.getpid(),'rootUri':root.as_uri(),'capabilities':{'workspace':{'configuration':True},'window':{'workDoneProgress':True},'textDocument':{'completion':{'completionItem':{'snippetSupport':False}}}},'workspaceFolders':[{'uri':root.as_uri(),'name':root.name}]})
 lsp.send({'jsonrpc':'2.0','method':'initialized','params':{}})
 file=root/'src/main/kotlin/OwnerCompletionMain.kt'
 lsp.send({'jsonrpc':'2.0','method':'textDocument/didOpen','params':{'textDocument':{'uri':file.as_uri(),'languageId':'kotlin','version':1,'text':file.read_text()}}})
 lsp.wait(lambda m:m.get('method')=='intellij/importLog' and m.get('params',{}).get('succeeded'),240)
 paths=lsp.call('workspace/executeCommand',{'command':'intellij.java.resolveBuildToolLaunch','arguments':[{'uri':file.as_uri(),'mainClass':'sample.OwnerCompletionMain'}]})
 port=lsp.call('workspace/executeCommand',{'command':'start_debug_server','arguments':[root.as_uri()]})
 sock=socket.create_connection(('127.0.0.1',port)); sock.settimeout(None)
 dap=Peer(sock.makefile('rb'),sock.makefile('wb'),'dap')
 dap.call('initialize',{'clientID':'vistudio-owner-test','adapterID':'intellij_jvm','linesStartAt1':True,'columnsStartAt1':True,'pathFormat':'path','supportsRunInTerminalRequest':False,'supportsVariableType':True})
 launch=dap.request('launch',{'type':'intellij_gradle','request':'launch','mainClass':'sample.OwnerCompletionMain','file':str(file),'cwd':str(root),'console':'internalConsole','classPaths':paths.get('scopeClassPaths',[]),'buildToolTarget':{'uri':file.as_uri(),'moduleName':paths.get('moduleName')}})
 dap.wait(lambda m:m.get('event')=='initialized')
 dap.call('setBreakpoints',{'source':{'path':str(file)},'breakpoints':[{'line':19},{'line':32}]})
 dap.call('configurationDone',{}); dap.response(launch)
 stopped=dap.wait(lambda m:m.get('event')=='stopped'); thread=stopped['body']['threadId']
 frames=dap.call('stackTrace',{'threadId':thread})['stackFrames']
 assert frames[0]['line']==19,frames
 for _ in range(2):
  complete(frames[0],'value.owner','ownerOuterValue')
  complete(frames[0],'items.owner','ownerOuterList')
 dap.call('continue',{'threadId':thread})
 stopped=dap.wait(lambda m:m.get('event')=='stopped'); thread=stopped['body']['threadId']
 frames=dap.call('stackTrace',{'threadId':thread})['stackFrames']
 assert frames[0]['line']==32 and frames[1]['line']==20,frames
 for index in [0,1,0,1]:
  owner='Inner' if index==0 else 'Outer'
  complete(frames[index],'value.owner','owner'+owner+'Value')
  complete(frames[index],'items.owner','owner'+owner+'List')
 count=dap.call('evaluate',{'expression':'counter.calls','frameId':frames[0]['id'],'context':'repl'})
 assert count['result']=='0',count
 dap.call('continue',{'threadId':thread})
 exited=dap.wait(lambda m:m.get('event')=='exited')
 assert exited['body']['exitCode']==0,exited
 print('EXIT',exited,flush=True)
 print('OUTPUT',json.dumps([m for m in dap.pending if m.get('event')=='output']),flush=True)
 dap.call('disconnect',{'terminateDebuggee':False})
 sock.close()
finally:
 subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],capture_output=True)
