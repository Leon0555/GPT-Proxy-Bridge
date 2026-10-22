using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace GPTProxyBridge.Core;

public static class Wire
{
    public static async Task<string> Header(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream(); byte[] one = new byte[1]; int end = 0;
        byte[] marker = [13,10,13,10];
        while (buffer.Length < 65536) {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("连接提前关闭。");
            buffer.WriteByte(one[0]); end = one[0] == marker[end] ? end + 1 : (one[0] == 13 ? 1 : 0);
            if (end == 4) return Encoding.Latin1.GetString(buffer.ToArray());
        }
        throw new IOException("HTTP 头超过 64 KB。");
    }
    public static Task Write(Stream stream, string value, CancellationToken ct) => stream.WriteAsync(Encoding.Latin1.GetBytes(value), ct).AsTask();
    public static int Status(string head) => int.TryParse(head.Split('\r')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var code) ? code : throw new IOException("代理返回无效 HTTP 状态。");
}

public static class Upstream
{
    public static async Task<Stream> Connect(ProxySnapshot snapshot, string host, int port, CancellationToken token)
    {
        var p = snapshot.Profile;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(p.TimeoutSeconds)); var ct = timeout.Token;
        var tcp = new TcpClient(); Stream? stream = null;
        try {
            await tcp.ConnectAsync(p.Host, p.Port, ct); stream = tcp.GetStream();
            if (p.Protocol == ProxyProtocol.HTTPS) {
                var ssl = new SslStream(stream, false); stream = ssl;
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = p.Host }, ct);
            }
            if (p.Protocol == ProxyProtocol.SOCKS5) await Socks(stream, snapshot, host, port, ct);
            else {
                string target = $"{(host.Contains(':') ? "["+host+"]" : host)}:{port}";
                string auth = p.Authentication ? "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Username + ":" + snapshot.Password)) + "\r\n" : "";
                await Wire.Write(stream, $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n{auth}\r\n", ct);
                int code = Wire.Status(await Wire.Header(stream, ct));
                if (code == 407) throw new IOException("代理认证失败（407），请检查账号密码或认证方式。");
                if (code != 200) throw new IOException($"上游拒绝 CONNECT 隧道（HTTP {code}）。");
            }
            return stream;
        } catch { stream?.Dispose(); tcp.Dispose(); throw; }
    }
    static async Task Socks(Stream stream, ProxySnapshot snapshot, string host, int port, CancellationToken ct)
    {
        var p = snapshot.Profile;
        await stream.WriteAsync(p.Authentication ? new byte[] {5,1,2} : new byte[] {5,1,0}, ct);
        byte[] reply = new byte[2]; await stream.ReadExactlyAsync(reply, ct);
        if (reply[0] != 5 || reply[1] != (p.Authentication ? 2 : 0)) throw new IOException("SOCKS5 认证方式不被代理支持。");
        if (p.Authentication) {
            byte[] user = Encoding.UTF8.GetBytes(p.Username), password = Encoding.UTF8.GetBytes(snapshot.Password);
            if (user.Length is < 1 or > 255 || password.Length is < 1 or > 255) throw new ArgumentException("SOCKS5 账号密码必须为 1–255 字节。");
            await stream.WriteAsync(new byte[] {1,(byte)user.Length}.Concat(user).Concat([(byte)password.Length]).Concat(password).ToArray(), ct);
            await stream.ReadExactlyAsync(reply, ct);
            if (reply[0] != 1 || reply[1] != 0) throw new IOException("SOCKS5 账号或密码错误。");
        }
        byte[] address;
        if (IPAddress.TryParse(host, out var ip)) address = new[] { (byte)(ip.AddressFamily == AddressFamily.InterNetwork ? 1 : 4) }.Concat(ip.GetAddressBytes()).ToArray();
        else { byte[] domain = Encoding.ASCII.GetBytes(new System.Globalization.IdnMapping().GetAscii(host)); if(domain.Length > 255) throw new ArgumentException("域名过长。"); address = new byte[]{3,(byte)domain.Length}.Concat(domain).ToArray(); }
        await stream.WriteAsync(new byte[]{5,1,0}.Concat(address).Concat([(byte)(port >> 8),(byte)port]).ToArray(), ct);
        byte[] response = new byte[4]; await stream.ReadExactlyAsync(response, ct);
        if (response[0] != 5 || response[1] != 0) throw new IOException($"SOCKS5 连接失败（代码 {response[1]}）。");
        int length = response[3] switch {1 => 4, 4 => 16, 3 => await ReadByte(stream,ct), _ => throw new IOException("SOCKS5 地址响应无效。")};
        await stream.ReadExactlyAsync(new byte[length + 2], ct);
    }
    static async Task<int> ReadByte(Stream stream,CancellationToken ct) {byte[] b=new byte[1];await stream.ReadExactlyAsync(b,ct);return b[0];}
    public static async Task<ProbeResult> Probe(ProxySnapshot snapshot, CancellationToken token = default, string target = "api.openai.com", int port = 443)
    {
        List<ProbeStep> steps = [];
        string stage = "地址解析";
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(snapshot.Profile.TimeoutSeconds));
            await Dns.GetHostAddressesAsync(snapshot.Profile.Host, timeout.Token); steps.Add(new(stage,true,"上游地址解析成功"));
            stage = "代理连接 / 认证";
            using var tunnel = await Connect(snapshot,target,port,timeout.Token); steps.Add(new(stage,true,"代理隧道建立成功"));
            stage = "目标 HTTPS";
            using var tls = new SslStream(tunnel,false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {TargetHost=target},timeout.Token);
            await Wire.Write(tls,$"GET /v1/models HTTP/1.1\r\nHost: {target}\r\nConnection: close\r\n\r\n",timeout.Token);
            int status=Wire.Status(await Wire.Header(tls,timeout.Token));
            bool acceptable=status is >=200 and <400 or 401;
            steps.Add(new(stage,acceptable,status == 401 ? "HTTPS 可达 · HTTP 401（未携带 API 密钥，非账号验证）" : $"HTTPS 可达 · HTTP {status}" + (acceptable ? "" : "；目标服务拒绝或异常，暂不应用配置")));
            return new(acceptable,steps);
        } catch(Exception ex) when(ex is not OutOfMemoryException) { steps.Add(new(stage,false,Friendly(ex))); return new(false,steps); }
    }
    public static string Friendly(Exception ex) => ex switch {
        OperationCanceledException => "连接超时或操作已取消，请检查网络和代理地址。",
        System.Security.Authentication.AuthenticationException => "TLS 证书验证失败，请检查代理协议、主机名和系统时间。",
        SocketException s when s.SocketErrorCode == SocketError.HostNotFound => "无法解析代理地址，请检查域名或 DNS。",
        SocketException s when s.SocketErrorCode == SocketError.AddressAlreadyInUse => "本地端口已被占用，请停止旧桥接或修改端口。",
        SocketException => "无法连接到代理，请检查地址、端口和防火墙。",
        _ => ex.Message
    };
}
