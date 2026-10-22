using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace GPTProxyBridge.Core;

public sealed class BridgeEngine : IDisposable
{
    TcpListener? listener;
    CancellationTokenSource? lifetime;
    ProxySnapshot? current;
    long sequence;
    readonly ConcurrentDictionary<long, int> counts = new();
    readonly ConcurrentDictionary<Guid, TcpClient> clients = new();
    public event Action<string>? Error;
    public RuntimeStatus Status => new(listener != null, (listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0,
        current?.Profile.Name, current?.Revision ?? 0, counts.Values.Sum(), counts.Where(x => x.Key != current?.Revision).Sum(x => x.Value));
    public void Apply(ProxyProfile profile, string password) => Interlocked.Exchange(ref current, new(profile.Normalize(),password,Interlocked.Increment(ref sequence)));
    public void Start(int port)
    {
        if(listener != null) return;
        if(current == null) throw new InvalidOperationException("请先选择并应用配置。");
        var candidate = new TcpListener(IPAddress.Loopback,port); candidate.Server.ExclusiveAddressUse=true;
        try { candidate.Start(); } catch {candidate.Stop();throw;}
        lifetime=new(); listener=candidate; _ = Accept(candidate,lifetime.Token);
    }
    async Task Accept(TcpListener server,CancellationToken ct)
    {
        try { while(!ct.IsCancellationRequested) {
            var client=await server.AcceptTcpClientAsync(ct);
            var snapshot=Volatile.Read(ref current)!;
            var id=Guid.NewGuid(); clients[id]=client; counts.AddOrUpdate(snapshot.Revision,1,(_,n)=>n+1);
            _ = Handle(client,snapshot,id,ct);
        }} catch(OperationCanceledException) {} catch(ObjectDisposedException) {} catch(SocketException) when(ct.IsCancellationRequested) {}
    }
    async Task Handle(TcpClient client,ProxySnapshot snapshot,Guid id,CancellationToken ct)
    {
        bool responded=false;
        try {
            {
                var downstream=client.GetStream();
                using var headerTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct); headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                string header=await Wire.Header(downstream,headerTimeout.Token);
                var lines=header.Split("\r\n",StringSplitOptions.None);
                var parts=lines[0].Split(' ',3);
                if(parts.Length != 3) throw new IOException("无效 HTTP 请求。");
                if(parts[0].Equals("CONNECT",StringComparison.OrdinalIgnoreCase)) {
                    if(!Uri.TryCreate("http://"+parts[1],UriKind.Absolute,out var target) || !string.IsNullOrEmpty(target.UserInfo) || target.AbsolutePath != "/") throw new IOException("无效 CONNECT 目标。");
                    using var remote=await Upstream.Connect(snapshot,target.Host.Trim('[',']'),target.Port,ct);
                    await Wire.Write(downstream,"HTTP/1.1 200 Connection Established\r\n\r\n",ct);responded=true;
                    using var copy=CancellationTokenSource.CreateLinkedTokenSource(ct);
                    Task a=downstream.CopyToAsync(remote,copy.Token), b=remote.CopyToAsync(downstream,copy.Token);
                    await Task.WhenAny(a,b); copy.Cancel();
                    try {await Task.WhenAll(a,b);}catch(OperationCanceledException){}
                } else {
                    if(!Uri.TryCreate(parts[1],UriKind.Absolute,out var target) || target.Scheme != "http" || target.UserInfo.Length > 0) throw new IOException("本地代理仅接受 HTTP 绝对地址和 CONNECT。");
                    var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    foreach(var line in lines.Skip(1).Where(x=>x.Length>0)) {
                        int colon=line.IndexOf(':'); if(colon<1) throw new IOException("HTTP 头格式错误。");
                        string key=line[..colon].Trim();
                        if(headers.ContainsKey(key) && (key.Equals("Content-Length",StringComparison.OrdinalIgnoreCase)||key.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase))) throw new IOException("重复的请求体长度头。");
                        headers[key]=headers.TryGetValue(key,out var prior) ? prior+", "+line[(colon+1)..].Trim() : line[(colon+1)..].Trim();
                    }
                    if(headers.ContainsKey("Content-Length") && headers.ContainsKey("Transfer-Encoding")) throw new IOException("请求体长度有歧义。");
                    using var handler=new SocketsHttpHandler {UseProxy=true,Proxy=new WebProxy(snapshot.Profile.Endpoint) {
                        Credentials=snapshot.Profile.Authentication && snapshot.Profile.Protocol==ProxyProtocol.SOCKS5 ? new NetworkCredential(snapshot.Profile.Username,snapshot.Password) : null
                    },AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(snapshot.Profile.TimeoutSeconds)};
                    using var http=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
                    using var request=new HttpRequestMessage(new HttpMethod(parts[0]),target){Version=HttpVersion.Version11};
                    if(snapshot.Profile.Authentication && snapshot.Profile.Protocol!=ProxyProtocol.SOCKS5)
                        request.Headers.TryAddWithoutValidation("Proxy-Authorization","Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(snapshot.Profile.Username+":"+snapshot.Password)));
                    if(headers.TryGetValue("Transfer-Encoding",out var transfer)) {
                        if(!transfer.Equals("chunked",StringComparison.OrdinalIgnoreCase)) throw new IOException("不支持该 Transfer-Encoding。");
                        request.Content=new StreamContent(new RequestBodyStream(downstream,null));
                    } else if(headers.TryGetValue("Content-Length",out var lengthText)) {
                        if(!long.TryParse(lengthText,out long length)||length<0)throw new IOException("Content-Length 无效。");
                        request.Content=new StreamContent(new RequestBodyStream(downstream,length));request.Content.Headers.ContentLength=length;
                    }
                    if(headers.TryGetValue("Expect",out var expect) && expect.Equals("100-continue",StringComparison.OrdinalIgnoreCase)) await Wire.Write(downstream,"HTTP/1.1 100 Continue\r\n\r\n",ct);
                    var excluded=new HashSet<string>(["Host","Connection","Proxy-Connection","Proxy-Authorization","Content-Length","Transfer-Encoding","Expect","Upgrade","TE","Trailer","Keep-Alive"],StringComparer.OrdinalIgnoreCase);
                    if(headers.TryGetValue("Connection",out var connection)) foreach(var name in connection.Split(','))excluded.Add(name.Trim());
                    foreach(var pair in headers.Where(x=>!excluded.Contains(x.Key))) if(!request.Headers.TryAddWithoutValidation(pair.Key,pair.Value))request.Content?.Headers.TryAddWithoutValidation(pair.Key,pair.Value);
                    using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
                    var text=new StringBuilder($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\nConnection: close\r\n");
                    foreach(var pair in response.Headers.Concat(response.Content.Headers)) if(!excluded.Contains(pair.Key)) text.Append(pair.Key).Append(": ").AppendJoin(", ",pair.Value).Append("\r\n");
                    text.Append("\r\n");await Wire.Write(downstream,text.ToString(),ct);responded=true;
                    if(parts[0] != "HEAD") await response.Content.CopyToAsync(downstream,ct);
                }
            }
        } catch(Exception ex) when(ex is not OutOfMemoryException) {
            if(!ct.IsCancellationRequested) Error?.Invoke(Upstream.Friendly(ex));
            if(!responded && client.Connected)try{await Wire.Write(client.GetStream(),"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",ct);}catch{}
        } finally {client.Dispose();clients.TryRemove(id,out _);counts.AddOrUpdate(snapshot.Revision,0,(_,n)=>Math.Max(0,n-1));}
    }
    public void Stop() {lifetime?.Cancel();listener?.Stop();listener=null;foreach(var client in clients.Values) client.Dispose();lifetime?.Dispose();lifetime=null;}
    public void Dispose()=>Stop();
}

// Decode chunked request bodies while streaming; never buffer an entire upload.
sealed class RequestBodyStream(Stream source,long? length) : Stream
{
    long remaining=length ?? 0; bool finished; bool chunkEnd;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
    {
        if(buffer.Length==0||finished)return 0;
        if(length==null && remaining==0) {
            if(chunkEnd && await Line(ct)!="")throw new IOException("无效 chunk 分隔符。");
            string line=await Line(ct);
            if(!long.TryParse(line.Split(';')[0],System.Globalization.NumberStyles.HexNumber,null,out remaining)||remaining<0)throw new IOException("无效 chunk 长度。");
            if(remaining==0){int total=0;while((line=await Line(ct)).Length>0){total+=line.Length;if(total>65536)throw new IOException("HTTP trailer 过长。");}finished=true;return 0;}
            chunkEnd=true;
        }
        if(remaining==0){finished=true;return 0;}
        int read=await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length,remaining)],ct);
        if(read==0)throw new IOException("请求体提前结束。");remaining-=read;return read;
    }
    async Task<string> Line(CancellationToken ct){var bytes=new List<byte>();byte[] b=new byte[1];while(bytes.Count<8192){await source.ReadExactlyAsync(b,ct);bytes.Add(b[0]);if(bytes.Count>=2&&bytes[^2]==13&&bytes[^1]==10)return Encoding.ASCII.GetString(bytes.ToArray(),0,bytes.Count-2);}throw new IOException("HTTP 行过长。");}
    public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead=>true;public override bool CanWrite=>false;public override bool CanSeek=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}
