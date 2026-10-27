using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GPTProxyBridge.App;

public sealed record UpdateCheckResult(string CurrentVersion, string LatestVersion, bool UpdateAvailable);

public sealed class UpdateChecker
{
    public const string LatestApiUrl="https://api.github.com/repos/Leon0555/GPT-Proxy-Bridge/releases/latest";
    public const string ReleasesUrl="https://github.com/Leon0555/GPT-Proxy-Bridge/releases/latest";
    static readonly HttpClient SharedClient=new(new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=TimeSpan.FromSeconds(8)};
    static readonly Regex VersionPattern=new(@"^v?(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$",RegexOptions.CultureInvariant);
    readonly HttpClient client;
    readonly string currentVersion;

    public static string CurrentVersion
    {
        get {
            var info=typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return (info??typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3)??"0.0.0").Split('+')[0];
        }
    }

    public UpdateChecker(HttpClient? client=null,string? currentVersion=null)
    {this.client=client??SharedClient;this.currentVersion=currentVersion??CurrentVersion;}

    static (Version Number,bool Prerelease) ParseVersion(string text)
    {
        var match=VersionPattern.Match(text);
        if(!match.Success || !int.TryParse(match.Groups[1].Value,out var major)
            || !int.TryParse(match.Groups[2].Value,out var minor) || !int.TryParse(match.Groups[3].Value,out var patch))
            throw new InvalidDataException("版本号格式无效。");
        return (new Version(major,minor,patch),match.Groups[4].Success);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken=default)
    {
        var current=ParseVersion(currentVersion);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var token=deadline.Token;
        using var request=new HttpRequestMessage(HttpMethod.Get,LatestApiUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GPTProxyBridge","1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is > 1024*1024)throw new InvalidDataException("版本信息过大。");
        await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer=new MemoryStream();
        var chunk=new byte[8192];
        int read;
        while((read=await stream.ReadAsync(chunk,token).ConfigureAwait(false))>0)
        {
            if(buffer.Length+read>1024*1024)throw new InvalidDataException("版本信息过大。");
            buffer.Write(chunk,0,read);
        }
        buffer.Position=0;
        using var doc=await JsonDocument.ParseAsync(buffer,cancellationToken:token).ConfigureAwait(false);
        var root=doc.RootElement;
        if(root.TryGetProperty("draft",out var draft) && draft.ValueKind==JsonValueKind.True
            || root.TryGetProperty("prerelease",out var pre) && pre.ValueKind==JsonValueKind.True)
            throw new InvalidDataException("最新版本不是正式版。");
        if(!root.TryGetProperty("tag_name",out var tag) || tag.ValueKind!=JsonValueKind.String)
            throw new InvalidDataException("缺少版本号。");
        string latestName=tag.GetString()!;
        var latest=ParseVersion(latestName);
        if(latest.Prerelease)throw new InvalidDataException("最新版本不是正式版。");
        int comparison=latest.Number.CompareTo(current.Number);
        return new(currentVersion,latest.Number.ToString(3),comparison>0 || comparison==0 && current.Prerelease);
    }
}
