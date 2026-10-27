using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using GPTProxyBridge.App;

internal static partial class Program
{
    static void UpdateRegression(string directory)
    {
        int requests=0;
        string json="{\"tag_name\":\"v1.2.0\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://invalid.example/\"}";
        var responseStatus=HttpStatusCode.OK;
        using var client=new HttpClient(new FakeUpdateHandler(request=>{
            requests++;
            Check(request.RequestUri?.AbsoluteUri==UpdateChecker.LatestApiUrl && request.Method==HttpMethod.Get
                && request.Headers.Authorization==null && !request.Headers.Contains("Cookie") && request.Headers.UserAgent.Count>0,
                "update request uses only the fixed public GitHub release endpoint");
            return new HttpResponseMessage(responseStatus){Content=new StringContent(json,Encoding.UTF8,"application/json")};
        }));
        var newer=new UpdateChecker(client,"1.1.0").CheckAsync().GetAwaiter().GetResult();
        Check(newer.UpdateAvailable && newer.LatestVersion=="1.2.0" && UpdateChecker.ReleasesUrl=="https://github.com/Leon0555/GPT-Proxy-Bridge/releases/latest",
            "new release is detected and browser destination is fixed to the project");
        Check(!new UpdateChecker(client,"1.2.0").CheckAsync().GetAwaiter().GetResult().UpdateAvailable
            && !new UpdateChecker(client,"1.3.0").CheckAsync().GetAwaiter().GetResult().UpdateAvailable,
            "equal or older releases do not trigger an update");
        Check(new UpdateChecker(client,"1.2.0-preview.1").CheckAsync().GetAwaiter().GetResult().UpdateAvailable,
            "stable release is newer than same-version preview");
        json="{\"tag_name\":\"v1.2.0-beta.1\",\"prerelease\":true}";
        try {new UpdateChecker(client,"1.1.0").CheckAsync().GetAwaiter().GetResult();throw new Exception("Prerelease accepted");}
        catch(InvalidDataException){Check(true,"prereleases are not offered as stable updates");}
        json="{\"tag_name\":\"not-a-version\"}";
        try {new UpdateChecker(client,"1.1.0").CheckAsync().GetAwaiter().GetResult();throw new Exception("Invalid version accepted");}
        catch(InvalidDataException){Check(true,"invalid release tags fail safely");}
        responseStatus=HttpStatusCode.Forbidden;
        try {new UpdateChecker(client,"1.1.0").CheckAsync().GetAwaiter().GetResult();throw new Exception("Failed HTTP request accepted");}
        catch(HttpRequestException){Check(true,"GitHub HTTP errors are reported as failures");}
        Check(requests==7,"mocked GitHub cases exercise all intended requests");
        if(Environment.GetEnvironmentVariable("GPT_PB_LIVE_UPDATE")=="1")
        {
            var live=new UpdateChecker().CheckAsync().GetAwaiter().GetResult();
            Check(Version.TryParse(live.LatestVersion,out _),"live GitHub latest-release check returns a stable version");
        }

        var pending=new TaskCompletionSource<UpdateCheckResult>();
        Task<UpdateCheckResult> next=pending.Task;
        int checks=0,notifications=0;
        var vm=new MainViewModel(Path.Combine(directory,"update-tests"),updateCheck:()=>{checks++;return next;});
        var window=new MainWindow(vm);
        try
        {
            window.Show();vm.SettingsVisible=true;vm.LocalPort="7996";Pump();
            var messageBefore=vm.Message;
            vm.UpdateAvailable+=_=>notifications++;
            var first=vm.CheckForUpdates(false);Pump();
            var button=(Button)window.FindName("CheckUpdatesButton");
            var label=(TextBlock)window.FindName("UpdateStatusText");
            Check(vm.UpdateChecking && !button.IsEnabled && label.Text.Contains("正在检查"),"automatic check displays progress and prevents duplicate requests");
            vm.CheckForUpdates(true).GetAwaiter().GetResult();
            Check(checks==1,"automatic and manual checks cannot overlap");
            pending.SetResult(new("1.1.0","1.2.0",true));Pump();first.GetAwaiter().GetResult();Pump();
            Check(!vm.UpdateChecking && button.IsEnabled && notifications==1 && label.Text.Contains("1.2.0")
                && vm.SettingsDirty && vm.LocalPort=="7996" && vm.Message==messageBefore && !vm.Engine.Status.Running,
                "available-update notification leaves proxy and unsaved settings intact");
            next=Task.FromResult(new UpdateCheckResult("1.1.0","1.1.0",false));
            vm.CheckUpdatesCommand.Execute(null);Pump();
            Check(notifications==1 && vm.UpdateStatus.Contains("最新版本") && checks==2,"manual check shows current version without opening a browser");
            next=Task.FromException<UpdateCheckResult>(new HttpRequestException("offline"));
            vm.CheckForUpdates(true).GetAwaiter().GetResult();Pump();
            Check(vm.UpdateStatus.Contains("失败") && !vm.UpdateChecking && notifications==1 && vm.SettingsDirty,
                "manual network failure is visible and does not affect proxy settings");
            vm.CheckForUpdates(false).GetAwaiter().GetResult();Pump();
            Check(vm.UpdateStatus.Contains("手动重试") && notifications==1,"automatic network failure stays nonblocking and supports retry");
            var editor=(ScrollViewer)window.FindName("EditorScroll");
            window.Width=1000;window.Height=780;Pump();
            Console.WriteLine($"Update settings layout: scroll={editor.ScrollableHeight}, viewport={editor.ViewportHeight}, extent={editor.ExtentHeight}");
            Capture(window,"07-update-settings");
            Check(editor.ScrollableHeight<1,"settings with update controls fits at the minimum window size");
        }
        finally {window.Hide();vm.Engine.Dispose();}
    }
}

internal sealed class FakeUpdateHandler(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        =>Task.FromResult(response(request));
}
