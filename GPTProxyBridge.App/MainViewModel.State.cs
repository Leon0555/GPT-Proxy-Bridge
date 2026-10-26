using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GPTProxyBridge.Core;

namespace GPTProxyBridge.App;

public sealed partial class MainViewModel
{
    readonly Func<string,MessageBoxResult> confirmUnsaved;
    readonly Func<ProxySnapshot,Task<ProbeResult>> probe;
    Guid draftId=Guid.NewGuid();
    readonly Dictionary<Guid,ProfileTestRecord> testHistory=new();
    Guid? testingId;
    Guid? testMessageId;
    string? appliedFingerprint;
    sealed record ProfileTestRecord(string Fingerprint,DateTimeOffset At,ProbeResult Result);

    string localPort="7995",bypass="localhost,127.0.0.1,::1",appPath="";
    public string LocalPort {get=>localPort;set{Set(ref localPort,value);SettingsChanged();}}
    public string Bypass {get=>bypass;set{Set(ref bypass,value);SettingsChanged();}}
    public string AppPath {get=>appPath;set{Set(ref appPath,value);SettingsChanged();}}
    public bool SettingsDirty=>LocalPort!=Data.Settings.LocalPort.ToString() || Bypass!=Data.Settings.Bypass || AppPath!=Data.Settings.AppPath;
    public bool HasSavedProfile=>selected!=null;
    public bool HasPendingApply=>selected!=null && (!Engine.Status.Running || activeId!=selected.Id ||
        appliedFingerprint!=ConfigurationFingerprint(selected,loadedPassword));
    public string ProfileStateText=>selected==null ? "新配置 · 尚未保存" : Dirty ? "有未保存的修改" :
        HasPendingApply ? "已保存，待应用" : "已应用 · 新连接正在使用";
    public string ConnectionHint=>selected==null || Dirty ? "先保存当前配置，再应用或启动" :
        HasPendingApply ? "已保存，点击应用配置才会生效" : "当前配置已应用";
    public bool CanManageProfile=>Available && EditorVisible && selected!=null;
    public bool CanApplyProfile=>Available && EditorVisible && selected!=null && !Dirty && ValidEditor;
    public bool CanDeleteProfile=>CanManageProfile && (!Engine.Status.Running || activeId!=selected!.Id);
    bool ValidEditor {get{try{Build();return true;}catch{return false;}}}

    bool TryLeaveEditor(string destination)
    {
        if(!Dirty)return true;
        var answer=confirmUnsaved($"当前配置有未保存的输入。是否保存后{destination}？\n\n是：保存并继续\n否：放弃修改\n取消：留在当前配置");
        if(answer==MessageBoxResult.No)return true;
        if(answer!=MessageBoxResult.Yes)return false;
        try {Save();return !Dirty;}
        catch(Exception ex){Message="无法保存，已保留当前输入："+Upstream.Friendly(ex);return false;}
    }
    public bool PrepareExit()
    {
        if(!TryLeaveEditor("退出程序"))return false;
        if(!SettingsDirty)return true;
        var answer=confirmUnsaved("连接设置尚未保存，是否保存后退出？\n\n是：保存并退出\n否：放弃修改并退出\n取消：继续使用程序");
        if(answer==MessageBoxResult.No)return true;
        if(answer!=MessageBoxResult.Yes)return false;
        try{SaveSettings();return true;}
        catch(Exception ex){Message="设置无法保存，已保留当前输入："+Upstream.Friendly(ex);return false;}
    }
    void RestoreSelection()
    {
        Notify(nameof(Selected));
        // WPF can be transferring SelectedItem while its setter rejects navigation.
        Application.Current.Dispatcher.InvokeAsync(()=>Notify(nameof(Selected)));
    }
    void SelectWithoutPrompt(ProxyProfile? profile)
    {
        if(selected==null)testHistory.Remove(draftId);
        if(profile==null)draftId=Guid.NewGuid();
        updatingSelection=true;
        try{selected=profile;Notify(nameof(Selected));}
        finally{updatingSelection=false;}
        Load();
    }

    void EditorChanged()
    {
        Notify(nameof(Dirty));
        UpdateTestFeedback();
        NotifyProfileState();
    }
    void NotifyProfileState()
    {
        foreach(var name in new[]{nameof(HasSavedProfile),nameof(HasPendingApply),nameof(ProfileStateText),nameof(ConnectionHint),
            nameof(CanManageProfile),nameof(CanApplyProfile),nameof(CanDeleteProfile)})Notify(name);
        InvalidateCommands();
    }
    void SettingsChanged(){Notify(nameof(SettingsDirty));InvalidateCommands();}
    void InvalidateCommands()
    {
        foreach(var command in new ICommand?[]{AddCommand,SaveCommand,DuplicateCommand,DeleteCommand,DefaultCommand,TestCommand,
            ApplyCommand,LaunchCommand,OpenAppCommand,StopCommand,SettingsCommand,SaveSettingsCommand,ImportCommand,ExportCommand}.OfType<AsyncCommand>())
            command.RaiseCanExecuteChanged();
    }

    static string NetworkFingerprint(ProxyProfile p,string secret)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new{p.Protocol,p.Host,p.Port,p.Authentication,
            Username=p.Authentication?p.Username:"",Password=p.Authentication?secret:"",p.TimeoutSeconds}))));
    static string ConfigurationFingerprint(ProxyProfile p,string secret)=>p.Name+":"+NetworkFingerprint(p,secret);
    string? EditorFingerprint(){try{return NetworkFingerprint(Build(),Password);}catch{return null;}}
    Guid EditorId=>selected?.Id??draftId;
    ProfileTestRecord? CurrentTest=>testHistory.TryGetValue(EditorId,out var result)?result:null;
    public bool HasTestRecord=>CurrentTest!=null;
    public bool HasTestFeedback=>testingId==EditorId || CurrentTest!=null || Results.Count>0;
    public string TestSummaryText=>CurrentTest is { } result ?
        $"最近测试 · {(string.IsNullOrWhiteSpace(ProfileName)?"新配置":ProfileName)} · {result.At.ToLocalTime():MM-dd HH:mm:ss}" : "";
    public string TestStatusText=>testingId==EditorId ? "正在测试当前配置…" : CurrentTest is { } result && result.Fingerprint!=EditorFingerprint() ?
        "配置已修改，需重新测试" : "";
    public bool HasTestStatus=>TestStatusText.Length>0;
    void UpdateTestFeedback()
    {
        Results.Clear();
        if(testingId!=EditorId && CurrentTest is { } result && result.Fingerprint==EditorFingerprint())
            foreach(var step in result.Result.Steps)Results.Add($"{(step.Success?"通过":"失败")} · {step.Name}：{step.Detail}");
        foreach(var name in new[]{nameof(HasTestRecord),nameof(HasTestFeedback),nameof(TestSummaryText),nameof(TestStatusText),nameof(HasTestStatus)})Notify(name);
        if(testMessageId.HasValue && testMessageId!=EditorId){Message="已切换配置；测试结果仅对应各自的配置。";testMessageId=null;}
        else if(testMessageId.HasValue && testingId!=EditorId && HasTestStatus)Message=TestStatusText;
    }
}
