using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using GPTProxyBridge.Core;

namespace GPTProxyBridge.App;
public sealed class Launcher
{
    public Process? OwnedProcess {get;private set;}
    public string? LaunchBypass {get;private set;}
    public int LaunchPort {get;private set;}
    public bool LastReusedExisting {get;private set;}
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window,int command);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    static bool RestoreWindow(string path)
    {
        foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        using(process)try {
            if(!string.Equals(process.MainModule?.FileName,path,StringComparison.OrdinalIgnoreCase))continue;
            process.Refresh();var window=process.MainWindowHandle;
            if(window==IntPtr.Zero)continue;
            ShowWindowAsync(window,IsIconic(window)?9:5);
            SetForegroundWindow(window);return true;
        } catch {}
        return false;
    }
    public bool OwnedRunning {get {try{return OwnedProcess is {HasExited:false};}catch{return false;}}}
    internal static async Task<string> PowerShell(string code)
    {
        var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
        info.ArgumentList.Add("-NoProfile");info.ArgumentList.Add("-NonInteractive");info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; "+code)));
        using var p=Process.Start(info) ?? throw new IOException("无法启动安装包查询。");
        Task<string> output=p.StandardOutput.ReadToEndAsync(), error=p.StandardError.ReadToEndAsync();
        try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));}catch{p.Kill(true);throw new IOException("系统查询超时。");}
        if(p.ExitCode!=0)throw new IOException("系统查询或旧凭据读取失败，请手动配置。");
        await error;return (await output).Trim();
    }
    public async Task<string> Discover(string manual)
    {
        if(!string.IsNullOrWhiteSpace(manual)) {if(!File.Exists(manual)||!manual.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new IOException("手动指定的 ChatGPT/Codex应用路径不存在或不是 EXE。");return Path.GetFullPath(manual);}
        string path=await PowerShell("$p=Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1; if($p){[xml]$m=Get-Content -LiteralPath (Join-Path $p.InstallLocation 'AppxManifest.xml') -Raw; $a=$m.Package.Applications.Application | Where-Object {$_.Executable -match '^app[\\/](ChatGPT|Codex)\\.exe$'} | Select-Object -First 1; if($a){Join-Path $p.InstallLocation $a.Executable}}");
        if(!File.Exists(path))throw new IOException("未找到 ChatGPT/Codex应用，请在设置中选择 ChatGPT/Codex应用路径。");return path;
    }
    public static bool HasRunning(string? manual=null)
    {
        var names=new[]{"ChatGPT","Codex",string.IsNullOrEmpty(manual)?"":Path.GetFileNameWithoutExtension(manual)}.Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach(var name in names)foreach(var p in Process.GetProcessesByName(name))using(p)try{
            string? path=p.MainModule?.FileName;
            if(path!=null && ((!string.IsNullOrWhiteSpace(manual) ? path.Equals(manual,StringComparison.OrdinalIgnoreCase) : Regex.IsMatch(path,@"\\OpenAI\.Codex_[^\\]+\\app\\(ChatGPT|Codex)\.exe$",RegexOptions.IgnoreCase))))return true;
        }catch{}
        return false;
    }
    public static ProcessStartInfo StartInfo(string path,int port,string bypass)
    {
        var info=new ProcessStartInfo(path){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(path)!};
        string proxy=$"http://127.0.0.1:{port}";
        foreach(var key in info.Environment.Keys.ToArray()) if(key.StartsWith("CODEX_UPSTREAM_PROXY_",StringComparison.OrdinalIgnoreCase)||key.StartsWith("CODEX_PROXY_BRIDGE_",StringComparison.OrdinalIgnoreCase))info.Environment.Remove(key);
        foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY"})info.Environment[key]=proxy;
        info.Environment["NO_PROXY"]=bypass;
        info.ArgumentList.Add("--proxy-server="+proxy);info.ArgumentList.Add("--proxy-bypass-list="+string.Join(';',bypass.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(x=>x=="::1"?"[::1]":x)));info.ArgumentList.Add("--disable-quic");return info;
    }
    public async Task Launch(AppSettings settings,int port)
    {
        var path=await Discover(settings.AppPath);
        LastReusedExisting=HasRunning(path);
        if(LastReusedExisting && RestoreWindow(path))return;
        // Electron may keep a background instance after its last window closes.
        // Running the same entrypoint asks its single-instance handler to open a window.
        var started=Process.Start(StartInfo(path,port,settings.Bypass)) ?? throw new IOException("ChatGPT/Codex应用未能启动。");
        await Task.Delay(3000);
        if(LastReusedExisting){started.Dispose();return;}
        OwnedProcess?.Dispose();OwnedProcess=started;
        if(OwnedProcess.HasExited)throw new IOException("ChatGPT/Codex应用启动后立即退出，请检查所选路径或启动兼容性。");
        LaunchPort=port;LaunchBypass=settings.Bypass;
    }
    public static async Task<(ProxyProfile Profile,string Password)> Import(string script)
    {
        string source=await File.ReadAllTextAsync(script);
        var host=Regex.Match(source,@"\$ProxyHost\s*=\s*['"" ]([^'""\r\n]+)['""]");
        var port=Regex.Match(source,@"\$ProxyPort\s*=\s*['"" ]?(\d+)");
        if(!host.Success||!port.Success)throw new IOException("无法解析旧脚本中的代理地址与端口。");
        string file=Path.Combine(Path.GetDirectoryName(script)!,"proxy-credential.clixml");
        if(!File.Exists(file))throw new IOException("未找到旧版加密凭据，请手动填写认证信息。");
        string escaped=file.Replace("'","''");
        string json=await PowerShell("$c=Import-Clixml -LiteralPath '"+escaped+"'; @{Username=$c.UserName;Password=$c.GetNetworkCredential().Password}|ConvertTo-Json -Compress");
        using var data=JsonDocument.Parse(json);
        var p=new ProxyProfile{Name="原代理配置",Host=host.Groups[1].Value.Trim(),Port=int.Parse(port.Groups[1].Value),Authentication=true,Username=data.RootElement.GetProperty("Username").GetString()??"",Protocol=host.Groups[1].Value.StartsWith("https:")?ProxyProtocol.HTTPS:ProxyProtocol.HTTP};
        return(p.Normalize(),data.RootElement.GetProperty("Password").GetString()??"");
    }
}
