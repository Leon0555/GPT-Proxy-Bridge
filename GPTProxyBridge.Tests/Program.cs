using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GPTProxyBridge.Core;
using GPTProxyBridge.App;

int passed=0,failed=0;
async Task Test(string name,Func<Task> test){try{await test().WaitAsync(TimeSpan.FromSeconds(15));Console.WriteLine("PASS "+name);passed++;}catch(Exception e){Console.WriteLine("FAIL "+name+": "+e);failed++;}}
void Assert(bool value,string message="Assertion failed"){if(!value)throw new Exception(message);}
ProxyProfile Profile(Mock proxy, bool auth=false)=>new(){Host=proxy.Host,Port=proxy.Port,Protocol=proxy.Protocol,Authentication=auth,Username=auth?"user":"",TimeoutSeconds=3};
async Task<string> Echo(Stream s,string value){await s.WriteAsync(Encoding.ASCII.GetBytes(value));byte[] b=new byte[value.Length+1];await s.ReadExactlyAsync(b);return Encoding.ASCII.GetString(b);}
async Task<TcpClient> Tunnel(BridgeEngine engine){var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,engine.Status.Port);await Wire.Write(c.GetStream(),"CONNECT example.com:443 HTTP/1.1\r\nHost: example.com\r\n\r\n",default);Assert(Wire.Status(await Wire.Header(c.GetStream(),default))==200);return c;}
foreach(var protocol in new[]{ProxyProtocol.HTTP,ProxyProtocol.SOCKS5})foreach(bool auth in new[]{false,true})await Test($"{protocol} tunnel auth={auth}",async()=>{using var mock=new Mock(protocol,auth,"A");using var s=await Upstream.Connect(new(Profile(mock,auth),"pass",1),"example.com",443,default);Assert(await Echo(s,"hello")=="Ahello");Assert(mock.LastTarget=="example.com");});
foreach(var protocol in new[]{ProxyProtocol.HTTP,ProxyProtocol.SOCKS5})await Test($"{protocol} rejects wrong credentials",async()=>{using var mock=new Mock(protocol,true,"A");try{using var s=await Upstream.Connect(new(Profile(mock,true),"wrong",1),"example.com",443,default);throw new Exception("Authenticated with wrong password");}catch(IOException ex){Assert(ex.Message.Contains("认证")||ex.Message.Contains("密码"));}});
await Test("hot switch preserves old tunnels",async()=>{
    using var a=new Mock(ProxyProtocol.HTTP,true,"A");using var b=new Mock(ProxyProtocol.SOCKS5,true,"B");using var engine=new BridgeEngine();engine.Apply(Profile(a,true),"pass");engine.Start(0);
    using var old=await Tunnel(engine);Assert(await Echo(old.GetStream(),"first")=="Afirst");engine.Apply(Profile(b,true),"pass");
    using var fresh=await Tunnel(engine);Assert(await Echo(fresh.GetStream(),"new")=="Bnew");Assert(await Echo(old.GetStream(),"old")=="Aold");Assert(engine.Status.OldConnections==1);Assert(engine.Status.Connections==2);
    engine.Apply(Profile(a,true),"pass");using var newest=await Tunnel(engine);Assert(await Echo(newest.GetStream(),"last")=="Alast");Assert(await Echo(fresh.GetStream(),"still")=="Bstill");engine.Stop();
});

int RevisionEntries(BridgeEngine engine) =>
    ((System.Collections.Concurrent.ConcurrentDictionary<long,int>)typeof(BridgeEngine)
        .GetField("counts",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
        .GetValue(engine)!).Count;
async Task Eventually(Func<bool> condition) {
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while(!condition())await Task.Delay(5,deadline.Token);
}
await Test("frequent switches reclaim revisions while preserving old connections",async()=>{
    using var a=new Mock(ProxyProtocol.HTTP,true,"A");
    using var b=new Mock(ProxyProtocol.SOCKS5,true,"B");
    using var engine=new BridgeEngine();
    engine.Apply(Profile(a,true),"pass");engine.Start(0);
    using var original=await Tunnel(engine);
    for(int i=0;i<100;i++){
        var proxy=i%2==0?b:a;var marker=i%2==0?"B":"A";
        engine.Apply(Profile(proxy,true),"pass");
        var batch=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Tunnel(engine)));
        try {
            Assert(engine.Status.Connections==9);
            Assert(engine.Status.OldConnections==1);
            await Task.WhenAll(batch.Select(async c=>Assert(await Echo(c.GetStream(),"x")==marker+"x")));
        } finally {foreach(var c in batch)c.Dispose();}
        await Eventually(()=>engine.Status.Connections==1 && RevisionEntries(engine)==1);
        Assert(RevisionEntries(engine)==1,"Completed revisions must be removed");
        Assert(await Echo(original.GetStream(),"old")=="Aold");
    }
    original.Dispose();
    await Eventually(()=>engine.Status.Connections==0 && RevisionEntries(engine)==0);
});
await Test("concurrent accept and close never lose active revision counts",async()=>{
    using var mock=new Mock(ProxyProtocol.HTTP,false,"A");
    using var engine=new BridgeEngine();engine.Apply(Profile(mock),"");engine.Start(0);
    // Repeatedly race the last closes against new accepts for the same revision.
    for(int round=0;round<50;round++){
        var closing=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Tunnel(engine)));
        var opening=Enumerable.Range(0,8).Select(_=>Tunnel(engine)).ToArray();
        await Task.WhenAll(closing.Select(c=>Task.Run(()=>c.Dispose())));
        var active=await Task.WhenAll(opening);
        try {
            await Eventually(()=>engine.Status.Connections==active.Length);
            Assert(RevisionEntries(engine)==1);
            await Task.WhenAll(active.Select(async c=>Assert(await Echo(c.GetStream(),"live")=="Alive")));
        } finally {foreach(var c in active)c.Dispose();}
        await Eventually(()=>engine.Status.Connections==0 && RevisionEntries(engine)==0);
    }
    var stopped=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Tunnel(engine)));
    engine.Stop();
    await Eventually(()=>engine.Status.Connections==0 && RevisionEntries(engine)==0);
    foreach(var c in stopped)c.Dispose();
});
await Test("bad upstream produces 502 without direct fallback",async()=>{using var mock=new Mock(ProxyProtocol.HTTP,true,"A");using var engine=new BridgeEngine();engine.Apply(Profile(mock,true),"wrong");engine.Start(0);using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,engine.Status.Port);await Wire.Write(client.GetStream(),"CONNECT example.com:443 HTTP/1.1\r\n\r\n",default);Assert(Wire.Status(await Wire.Header(client.GetStream(),default))==502);});
await Test("port conflict refuses second listener",()=>{using var mock=new Mock(ProxyProtocol.HTTP,false,"A");using var a=new BridgeEngine();using var b=new BridgeEngine();a.Apply(Profile(mock),"");a.Start(0);b.Apply(Profile(mock),"");try{b.Start(a.Status.Port);throw new Exception("port stolen");}catch(SocketException){}return Task.CompletedTask;});
foreach(bool chunked in new[]{false,true})await Test("HTTP upload streaming chunked="+chunked,async()=>{using var mock=new Mock(ProxyProtocol.HTTP,false,"A");using var engine=new BridgeEngine();engine.Apply(Profile(mock),"");engine.Start(0);using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,engine.Status.Port);string body=chunked?"5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n":"hello world";await Wire.Write(client.GetStream(),"POST http://example.com/upload HTTP/1.1\r\nHost: example.com\r\n"+(chunked?"Transfer-Encoding: chunked":"Content-Length: 11")+"\r\n\r\n"+body,default);string head=await Wire.Header(client.GetStream(),default);Assert(Wire.Status(head)==200);using var reader=new StreamReader(client.GetStream());Assert(await reader.ReadToEndAsync()=="hello world");Assert(mock.LastBody=="hello world");});
await Test("TLS upstream rejects invalid certificate",async()=>{using var mock=new Mock(ProxyProtocol.HTTPS,false,"A");try{using var s=await Upstream.Connect(new(Profile(mock),"",1),"example.com",443,default);throw new Exception("Invalid certificate accepted");}catch(System.Security.Authentication.AuthenticationException){}});
await Test("HTTP authenticated upload supplies credentials without replay",async()=>{using var mock=new Mock(ProxyProtocol.HTTP,true,"A");using var engine=new BridgeEngine();engine.Apply(Profile(mock,true),"pass");engine.Start(0);using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,engine.Status.Port);await Wire.Write(client.GetStream(),"POST http://example.com/upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\nhello",default);Assert(Wire.Status(await Wire.Header(client.GetStream(),default))==200);Assert(mock.LastBody=="hello");});
await Test("stop releases local port",async()=>{using var mock=new Mock(ProxyProtocol.HTTP,false,"A");using var engine=new BridgeEngine();engine.Apply(Profile(mock),"");engine.Start(0);int port=engine.Status.Port;engine.Stop();var other=new TcpListener(IPAddress.Loopback,port);other.Start();other.Stop();await Task.CompletedTask;});
await Test("IPv6 upstream and SOCKS target",async()=>{using var mock=new Mock(ProxyProtocol.SOCKS5,false,"V",true);using var s=await Upstream.Connect(new(Profile(mock),"",1),"::1",443,default);Assert(await Echo(s,"six")=="Vsix");Assert(mock.LastTarget=="::1");});
await Test("address validation and normalization",()=>{Assert((new ProxyProfile{Host="http://example.com",Port=80}).Normalize().Host=="example.com");Assert((new ProxyProfile{Host="[::1]"}).Normalize().Host=="::1");foreach(string host in new[]{"http://u:p@example.com","http://example.com/path","bad host"}){try{_=(new ProxyProfile{Host=host}).Normalize();throw new Exception("Accepted "+host);}catch(ArgumentException){}}return Task.CompletedTask;});
await Test("DPAPI credential storage excludes JSON",()=>{string dir=Path.Combine(Path.GetTempPath(),"GPTProxyBridge-tests-"+Guid.NewGuid());var store=new ConfigurationStore(dir);var p=new ProxyProfile{Authentication=true,Username="user"};p=p with{CredentialReference=store.SavePassword(p.Id,"secret-test-value")};store.Save(new Configuration{Profiles=[p]});Assert(store.ReadPassword(p)=="secret-test-value");Assert(!File.ReadAllText(Path.Combine(dir,"profiles.json")).Contains("secret-test-value"));Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir,p.CredentialReference))).Contains("secret-test-value"));Assert(store.Load().Profiles.Count==1);return Task.CompletedTask;});
await Test("launcher environment contains no upstream secret",()=>{Environment.SetEnvironmentVariable("CODEX_UPSTREAM_PROXY_PASSWORD","private-value");try{var info=Launcher.StartInfo(@"C:\Example\app.exe",7995,"localhost,127.0.0.1,::1");Assert(!info.Environment.ContainsKey("CODEX_UPSTREAM_PROXY_PASSWORD"));Assert(info.Environment["HTTPS_PROXY"]=="http://127.0.0.1:7995");Assert(info.ArgumentList.Contains("--proxy-server=http://127.0.0.1:7995"));}finally{Environment.SetEnvironmentVariable("CODEX_UPSTREAM_PROXY_PASSWORD",null);}return Task.CompletedTask;});
if(args.Contains("--installed"))await Test("installed package discovery",async()=>{string path=await new Launcher().Discover("");Assert(File.Exists(path));Assert(path.Contains("OpenAI.Codex_"));Console.WriteLine("  Found current desktop package (path verified).");});
if(args.Contains("--live"))await Test("legacy migration and real HTTPS proxy",async()=>{var(p,password)=await Launcher.Import(Environment.GetEnvironmentVariable("GPT_PB_LEGACY_SCRIPT") ?? throw new InvalidOperationException("Set GPT_PB_LEGACY_SCRIPT explicitly for --live."));var result=await Upstream.Probe(new(p,password,1));foreach(var step in result.Steps)Console.WriteLine("  "+step.Name+": "+step.Detail);Assert(result.Success);});
Console.WriteLine($"RESULT: {passed} passed, {failed} failed");return failed==0?0:1;

sealed class Mock : IDisposable
{
    readonly TcpListener listener;readonly CancellationTokenSource stop=new();readonly bool auth;readonly string marker;readonly X509Certificate2? cert;
    public ProxyProtocol Protocol{get;}public string Host{get;}public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;public string LastTarget="",LastBody="";
    public Mock(ProxyProtocol protocol,bool auth,string marker,bool ipv6=false){Protocol=protocol;this.auth=auth;this.marker=marker;Host=ipv6?"::1":"127.0.0.1";listener=new(IPAddress.Parse(Host),0);listener.Start();if(protocol==ProxyProtocol.HTTPS){using var rsa=RSA.Create(2048);var req=new CertificateRequest("CN=localhost",rsa,System.Security.Cryptography.HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);using var generated=req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1),DateTimeOffset.Now.AddDays(1));cert=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.Exportable);}_=Loop();}
    async Task Loop(){try{while(!stop.IsCancellationRequested){var c=await listener.AcceptTcpClientAsync(stop.Token);_=Handle(c);}}catch(OperationCanceledException){}catch(ObjectDisposedException){}}
    async Task Handle(TcpClient c){using(c)try{Stream s=c.GetStream();if(Protocol==ProxyProtocol.HTTPS){var tls=new SslStream(s);s=tls;await tls.AuthenticateAsServerAsync(cert!);}
        if(Protocol==ProxyProtocol.SOCKS5){byte[] hello=new byte[2];await s.ReadExactlyAsync(hello);byte[] methods=new byte[hello[1]];await s.ReadExactlyAsync(methods);await s.WriteAsync(new byte[]{5,(byte)(auth?2:0)});if(auth){byte[] first=new byte[2];await s.ReadExactlyAsync(first);var u=new byte[first[1]];await s.ReadExactlyAsync(u);byte[] len=new byte[1];await s.ReadExactlyAsync(len);var p=new byte[len[0]];await s.ReadExactlyAsync(p);bool ok=Encoding.UTF8.GetString(u)=="user"&&Encoding.UTF8.GetString(p)=="pass";await s.WriteAsync(new byte[]{1,(byte)(ok?0:1)});if(!ok)return;}byte[] head=new byte[4];await s.ReadExactlyAsync(head);int n=head[3]==1?4:head[3]==4?16:await ReadByte(s);byte[] addr=new byte[n];await s.ReadExactlyAsync(addr);LastTarget=head[3]==3?Encoding.ASCII.GetString(addr):new IPAddress(addr).ToString();await s.ReadExactlyAsync(new byte[2]);await s.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,80});}
        else {string head=await Wire.Header(s,default);if(auth&&!head.Contains("Proxy-Authorization: Basic dXNlcjpwYXNz",StringComparison.OrdinalIgnoreCase)){await Wire.Write(s,"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=mock\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",default);return;}
            if(head.StartsWith("CONNECT")){LastTarget=head.Split(' ')[1].Split(':')[0];await Wire.Write(s,"HTTP/1.1 200 Connection Established\r\n\r\n",default);}else{string body="";if(head.Contains("Transfer-Encoding: chunked",StringComparison.OrdinalIgnoreCase)){while(true){string line=await Line(s);int len=Convert.ToInt32(line,16);if(len==0){await Line(s);break;}byte[] b=new byte[len];await s.ReadExactlyAsync(b);body+=Encoding.UTF8.GetString(b);await Line(s);}}else{var match=System.Text.RegularExpressions.Regex.Match(head,@"Content-Length:\s*(\d+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);if(match.Success){byte[] b=new byte[int.Parse(match.Groups[1].Value)];await s.ReadExactlyAsync(b);body=Encoding.UTF8.GetString(b);}}LastBody=body;await Wire.Write(s,$"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}",default);return;}}
        byte[] buffer=new byte[4096];while(true){int n=await s.ReadAsync(buffer,stop.Token);if(n==0)return;await s.WriteAsync(Encoding.ASCII.GetBytes(marker));await s.WriteAsync(buffer.AsMemory(0,n));}
    }catch(Exception){} }
    static async Task<int> ReadByte(Stream s){byte[] b=new byte[1];await s.ReadExactlyAsync(b);return b[0];}
    static async Task<string> Line(Stream s){var b=new List<byte>();while(true){b.Add((byte)await ReadByte(s));if(b.Count>=2&&b[^2]==13&&b[^1]==10)return Encoding.ASCII.GetString(b.ToArray(),0,b.Count-2);}}
    public void Dispose(){stop.Cancel();listener.Stop();cert?.Dispose();stop.Dispose();}
}
