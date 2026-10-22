using System.Net;
using System.Text.Json.Serialization;

namespace GPTProxyBridge.Core;

public enum ProxyProtocol { HTTP, HTTPS, SOCKS5 }
public sealed record ProxyProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "新配置";
    public ProxyProtocol Protocol { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 7890;
    public bool Authentication { get; init; }
    public string Username { get; init; } = "";
    public string CredentialReference { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 20;
    public ProxyProfile Normalize()
    {
        string host = Host.Trim();
        if (host.Contains("://")) {
            if (!Uri.TryCreate(host, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("地址只能包含协议和主机；请将账号密码填写到认证区域。");
            if (!uri.Scheme.Equals(Protocol.ToString(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("地址中的协议与所选协议不同。");
            if (!uri.IsDefaultPort && uri.Port != Port) throw new ArgumentException("地址内的端口与端口栏不同，请只填写主机名。");
            host = uri.Host;
        }
        host = host.Trim('[', ']');
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Any(char.IsWhiteSpace)) throw new ArgumentException("请输入有效的域名、IPv4 或 IPv6 地址。");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("请填写配置名称。");
        if (Port is < 1 or > 65535) throw new ArgumentException("端口必须为 1–65535。");
        if (TimeoutSeconds is < 3 or > 120) throw new ArgumentException("连接超时必须为 3–120 秒。");
        if (Authentication && (string.IsNullOrEmpty(Username) || Username.Contains(':') || Username.Any(char.IsControl))) throw new ArgumentException("请填写有效账号（不能包含冒号或控制字符）。");
        return this with { Name = Name.Trim(), Host = host };
    }
    [JsonIgnore] public string Endpoint => $"{Protocol.ToString().ToLowerInvariant()}://{(Host.Contains(':') ? "[" + Host + "]" : Host)}:{Port}";
}
// Credentials exist only in memory while a connection revision needs them.
public sealed record ProxySnapshot(ProxyProfile Profile, string Password, long Revision)
{
    public override string ToString() => $"ProxySnapshot {{ ProfileId = {Profile.Id}, Revision = {Revision}, Password = [redacted] }}";
}
public sealed record AppSettings
{
    public Guid? DefaultProfileId { get; set; }
    public int LocalPort { get; set; } = 7995;
    public string Bypass { get; set; } = "localhost,127.0.0.1,::1";
    public string Theme { get; set; } = "System";
    public string AppPath { get; set; } = "";
    public bool MigrationOffered { get; set; }
}
public sealed record Configuration
{
    public int Version { get; init; } = 1;
    public List<ProxyProfile> Profiles { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}
public sealed record ProbeStep(string Name, bool Success, string Detail);
public sealed record ProbeResult(bool Success, IReadOnlyList<ProbeStep> Steps);
public sealed record RuntimeStatus(bool Running, int Port, string? ActiveName, long Revision, int Connections, int OldConnections);
