using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace GPTProxyBridge.App;

public sealed partial class MainViewModel
{
    readonly Func<Task<UpdateCheckResult>> updateCheck;
    bool updateChecking;
    string updateStatus="启动时自动检查，也可手动检查。";
    public ICommand CheckUpdatesCommand {get;private set;}=null!;
    public event Action<UpdateCheckResult>? UpdateAvailable;
    public string CurrentVersionText=>"当前版本："+UpdateChecker.CurrentVersion;
    public string UpdateStatus {get=>updateStatus;private set=>Set(ref updateStatus,value);}
    public bool UpdateChecking
    {
        get=>updateChecking;
        private set {Set(ref updateChecking,value);if(CheckUpdatesCommand is AsyncCommand command)command.RaiseCanExecuteChanged();}
    }

    public async Task CheckForUpdates(bool manual)
    {
        if(UpdateChecking)return;
        UpdateChecking=true;
        UpdateStatus="正在检查更新…";
        try
        {
            var result=await updateCheck();
            if(result.UpdateAvailable)
            {
                UpdateStatus="发现新版本 "+result.LatestVersion+"，可前往 GitHub 下载。";
                UpdateAvailable?.Invoke(result);
            }
            else UpdateStatus="当前已是最新版本（GitHub 正式版 "+result.LatestVersion+"）。";
        }
        catch(Exception)
        {
            UpdateStatus=manual?"检查更新失败，请检查网络后重试。":"自动检查更新未完成，可在此手动重试。";
        }
        finally {UpdateChecking=false;}
    }
}
