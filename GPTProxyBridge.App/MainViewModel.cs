using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GPTProxyBridge.Core;

namespace GPTProxyBridge.App;
public sealed class AsyncCommand(Func<Task> action, Func<bool>? enabled=null) : ICommand
{
    bool running; public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged()=>CanExecuteChanged?.Invoke(this,EventArgs.Empty);
    public bool CanExecute(object? p)=>!running&&(enabled?.Invoke()??true);
    public async void Execute(object? p){if(!CanExecute(p))return;running=true;CanExecuteChanged?.Invoke(this,EventArgs.Empty);try{await action();}catch(Exception ex){MessageBox.Show(Upstream.Friendly(ex),"GPT Proxy Bridge");}finally{running=false;CanExecuteChanged?.Invoke(this,EventArgs.Empty);}}
}
public sealed partial class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    void Set<T>(ref T field,T value,[CallerMemberName]string? name=null){if(Equals(field,value))return;field=value;Notify(name);}
    public readonly BridgeEngine Engine=new(); public readonly Launcher Launcher=new();
    readonly ConfigurationStore store;
    public Configuration Data {get;}
    public ObservableCollection<ProxyProfile> Profiles {get;}=new();
    public ObservableCollection<string> Results {get;}=new();
    readonly SemaphoreSlim gate=new(1);
    ProxyProfile? selected;
    bool updatingSelection;
    public ProxyProfile? Selected {get=>selected;set{
        if(updatingSelection)return;
        if(selected==value){if(value!=null)SettingsVisible=false;return;}
        if(!TryLeaveEditor("切换配置")){RestoreSelection();return;}
        SelectWithoutPrompt(value);
        if(value!=null)SettingsVisible=false;
    }}
    string name="",host="127.0.0.1",port="7890",username="",password="",timeout="20",message="添加配置，或导入已有的代理设置。"; bool auth,busy,settings;
    ProxyProtocol protocol;
    public string ProfileName{get=>name;set{Set(ref name,value);EditorChanged();}}
    public string Host{get=>host;set{Set(ref host,value);EditorChanged();}}
    public string Port{get=>port;set{Set(ref port,value);EditorChanged();}}
    public string Username{get=>username;set{Set(ref username,value);EditorChanged();}}
    public string Password{get=>password;set{Set(ref password,value);EditorChanged();}}
    public string Timeout{get=>timeout;set{Set(ref timeout,value);EditorChanged();}}
    public bool Authentication{get=>auth;set{Set(ref auth,value);EditorChanged();}}
    public ProxyProtocol Protocol{get=>protocol;set{Set(ref protocol,value);EditorChanged();}}
    public Array Protocols=>Enum.GetValues(typeof(ProxyProtocol));
    public bool Busy{get=>busy;set{Set(ref busy,value);Notify(nameof(Available));NotifyProfileState();}}
    public bool Available=>!Busy;
    public bool SettingsVisible{get=>settings;set{bool entering=value&&!settings,leaving=!value&&settings;Set(ref settings,value);if(entering){if(!Message.StartsWith("最近连接错误：",StringComparison.Ordinal))Message="连接设置修改后请点击“保存设置”。";testMessageId=null;}else if(leaving&&Message=="连接设置修改后请点击“保存设置”。")Message=selected==null?"填写配置后点击保存即可。":"选择配置后可测试或应用。";Notify(nameof(EditorVisible));NotifyProfileState();}}
    public bool EditorVisible=>!settings;
    public string Message{get=>message;set=>Set(ref message,value);}
    string loadedPassword="";
    public bool Dirty=>selected==null ? (name.Length>0||host!="127.0.0.1"||port!="7890"||auth||username.Length>0||password.Length>0||timeout!="20"||protocol!=ProxyProtocol.HTTP) : (name!=selected.Name||host!=selected.Host||port!=selected.Port.ToString()||username!=selected.Username||auth!=selected.Authentication||protocol!=selected.Protocol||timeout!=selected.TimeoutSeconds.ToString()||password!=loadedPassword);
    string theme="System";
    public string Theme{get=>theme;set{if(value is not ("System" or "Light" or "Dark") || theme==value)return;Set(ref theme,value);Data.Settings.Theme=value;Persist();if(Application.Current is App app)app.SetTheme(value);}}
    public string[] Themes=>["System","Light","Dark"];
    public bool IsBridgeRunning=>Engine.Status.Running;
    public string RuntimeText {get {var s=Engine.Status;return s.Running?$"本地桥接运行中 · 127.0.0.1:{s.Port}  /  {s.Connections} 条连接":"本地桥接未启动";}}
    public string ActiveText=>Engine.Status.Running && Engine.Status.ActiveName is { } n?$"生效配置：{n} · 旧连接 {Engine.Status.OldConnections} 条":"生效配置：无";
    bool appRunning,appOwned;
    public bool IsAppRunning=>appRunning;
    public string AppText=>appRunning?"ChatGPT/Codex应用已运行":"ChatGPT/Codex应用未运行";
    public string AppSourceText=>!appRunning?"":appOwned?"由本软件启动":"未由本软件启动";
    public string DefaultText=>Selected!=null && Data.Settings.DefaultProfileId==Selected.Id?"默认配置":"设为默认";
    public Guid? ActiveProfileId=>Engine.Status.Running?activeId:null;
    public Guid? DefaultProfileId=>Data.Settings.DefaultProfileId;
    public ICommand OpenAppCommand{get;} public ICommand AddCommand{get;} public ICommand SaveCommand{get;} public ICommand DuplicateCommand{get;} public ICommand DeleteCommand{get;} public ICommand DefaultCommand{get;} public ICommand TestCommand{get;} public ICommand ApplyCommand{get;} public ICommand LaunchCommand{get;} public ICommand StopCommand{get;} public ICommand SettingsCommand{get;} public ICommand SaveSettingsCommand{get;} public ICommand ImportCommand{get;} public ICommand ExportCommand{get;}
    public MainViewModel(string directory, Func<string,MessageBoxResult>? confirmUnsaved=null, Func<ProxySnapshot,Task<ProbeResult>>? probe=null)
    {
        this.confirmUnsaved=confirmUnsaved ?? (text=>MessageBox.Show(text,"未保存的配置",MessageBoxButton.YesNoCancel,MessageBoxImage.Question));
        this.probe=probe ?? (snapshot=>Upstream.Probe(snapshot));
        store=new(directory);Data=store.Load();foreach(var p in Data.Profiles)Profiles.Add(p);
        Results.CollectionChanged+=(_,_)=>Notify(nameof(HasTestFeedback));
        LocalPort=Data.Settings.LocalPort.ToString();Bypass=Data.Settings.Bypass;AppPath=Data.Settings.AppPath;theme=Data.Settings.Theme;
        AddCommand=new AsyncCommand(()=>{if(!TryLeaveEditor("新增配置"))return Task.CompletedTask;draftId=Guid.NewGuid();SelectWithoutPrompt(null);SettingsVisible=false;Message="填写配置后点击保存即可。";return Task.CompletedTask;},()=>Available);
        SaveCommand=new AsyncCommand(()=>{Save();return Task.CompletedTask;},()=>Available && EditorVisible && ValidEditor);
        DuplicateCommand=new AsyncCommand(()=>{if(selected!=null){var p=Build() with{Id=Guid.NewGuid(),Name=ProfileName+" 副本",CredentialReference=""};string pass=Password;if(p.Authentication)p=p with{CredentialReference=store.SavePassword(p.Id,pass)};Profiles.Add(p);SelectWithoutPrompt(p);Persist();}return Task.CompletedTask;},()=>CanManageProfile && ValidEditor);
        DeleteCommand=new AsyncCommand(()=>{
            if(selected==null)return Task.CompletedTask;
            if(MessageBox.Show("删除此配置、本机保存的密码及尚未保存的修改？","删除配置",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return Task.CompletedTask;
            var id=selected.Id;
            updatingSelection=true;try{Profiles.Remove(selected);}finally{updatingSelection=false;}
            testHistory.Remove(id);SelectWithoutPrompt(Profiles.FirstOrDefault());
            if(Data.Settings.DefaultProfileId==id)Data.Settings.DefaultProfileId=null;
            Persist();store.DeletePassword(id);Notify(nameof(DefaultText));return Task.CompletedTask;
        },()=>CanDeleteProfile);
        DefaultCommand=new AsyncCommand(()=>{if(selected!=null){Data.Settings.DefaultProfileId=selected.Id;Persist();Notify(nameof(DefaultText));}return Task.CompletedTask;},()=>CanManageProfile && !Dirty && Data.Settings.DefaultProfileId!=selected!.Id);
        TestCommand=new AsyncCommand(()=>Run(async()=>{var p=Build();await Test(new(p,Password,0));}),()=>Available && EditorVisible && ValidEditor);
        ApplyCommand=new AsyncCommand(()=>ApplySelected(false),()=>CanApplyProfile);
        LaunchCommand=new AsyncCommand(()=>ApplySelected(true),()=>CanApplyProfile);
        OpenAppCommand=new AsyncCommand(()=>Run(async()=>{
            if(!Engine.Status.Running && !Launcher.HasRunning(Data.Settings.AppPath))throw new IOException("请先应用代理配置，或使用“连接并启动 ChatGPT/Codex”。");
            await OpenApplication();
        }));
        StopCommand=new AsyncCommand(()=>{Stop();return Task.CompletedTask;},()=>Available && Engine.Status.Running);
        SettingsCommand=new AsyncCommand(()=>{SettingsVisible=true;return Task.CompletedTask;},()=>Available);
        SaveSettingsCommand=new AsyncCommand(()=>{SaveSettings();return Task.CompletedTask;},()=>Available && SettingsDirty);
        ImportCommand=new AsyncCommand(async()=>{if(TryLeaveEditor("导入配置"))await Run(Import);},()=>Available);
        ExportCommand=new AsyncCommand(()=>{var dialog=new Microsoft.Win32.SaveFileDialog{Filter="JSON 配置|*.json",FileName="GPT-Proxy-Bridge-profiles.json"};if(dialog.ShowDialog()==true){var safe=new Configuration{Profiles=Profiles.Select(x=>x with{CredentialReference=""}).ToList(),Settings=new AppSettings{DefaultProfileId=Data.Settings.DefaultProfileId,LocalPort=Data.Settings.LocalPort,Bypass=Data.Settings.Bypass}};File.WriteAllText(dialog.FileName,System.Text.Json.JsonSerializer.Serialize(safe,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Message="配置已导出（不含密码）。";}return Task.CompletedTask;});
        selected=Profiles.FirstOrDefault(p=>p.Id==Data.Settings.DefaultProfileId)??Profiles.FirstOrDefault();Load();
        Engine.Error+=error=>Application.Current.Dispatcher.InvokeAsync(()=>Message="最近连接错误："+error);
        Refresh();
    }
    Guid? activeId;
    void Load(){if(selected!=null){name=selected.Name;host=selected.Host;port=selected.Port.ToString();username=selected.Username;auth=selected.Authentication;protocol=selected.Protocol;timeout=selected.TimeoutSeconds.ToString();try{password=store.ReadPassword(selected);}catch{password="";Message="无法读取此配置密码，请重新填写后保存。";}loadedPassword=password;}else{name="";host="127.0.0.1";port="7890";username="";auth=false;protocol=ProxyProtocol.HTTP;timeout="20";password="";loadedPassword="";}foreach(var property in new[]{nameof(ProfileName),nameof(Host),nameof(Port),nameof(Username),nameof(Authentication),nameof(Protocol),nameof(Timeout),nameof(Password),nameof(Dirty),nameof(DefaultText)})Notify(property);EditorChanged();}
    ProxyProfile Build(){if(!int.TryParse(Port,out var n)||!int.TryParse(Timeout,out var t))throw new IOException("端口和超时必须为数字。");if(auth&&string.IsNullOrEmpty(password))throw new IOException("已开启认证，请填写密码。");return((selected??new ProxyProfile{Id=draftId}) with{Name=string.IsNullOrWhiteSpace(name)?"新配置":name,Host=host,Port=n,Username=username,Authentication=auth,Protocol=protocol,TimeoutSeconds=t}).Normalize();}
    void Persist(){Data.Profiles=Profiles.ToList();store.Save(Data);Notify(nameof(DefaultProfileId));InvalidateCommands();}
    public void Save()
    {
        if(Busy)return;
        var p=Build();
        if(p.Authentication)p=p with{CredentialReference=store.SavePassword(p.Id,Password)};
        else p=p with{CredentialReference=""};
        int index=selected==null?-1:Profiles.ToList().FindIndex(x=>x.Id==selected.Id);
        // Replacing a bound ListBox item synchronously pushes null back through SelectedItem.
        // Ignore that transient update, then re-select the exact replacement instance.
        updatingSelection=true;
        try {if(index>=0)Profiles[index]=p;else Profiles.Add(p);selected=p;}
        finally {updatingSelection=false;}
        Persist();Load();Notify(nameof(Selected));
        Message="已保存；点击“应用配置”才会切换连接。";
    }
    async Task Run(Func<Task> action){if(!await gate.WaitAsync(0)){Message="另一项操作正在进行，请稍候。";return;}Busy=true;try{await action();}finally{Busy=false;gate.Release();Refresh();}}
    async Task<bool> Test(ProxySnapshot snapshot)
    {
        testingId=snapshot.Profile.Id;testMessageId=snapshot.Profile.Id;UpdateTestFeedback();Message="正在测试代理与目标 HTTPS…";
        try{
            var result=await probe(snapshot);
            testHistory[snapshot.Profile.Id]=new(NetworkFingerprint(snapshot.Profile,snapshot.Password),DateTimeOffset.Now,result);
            Message=result.Success?"连接测试通过。未验证 ChatGPT/Codex应用登录或所有服务。":"测试未通过；当前生效配置保持不变。";
            return result.Success;
        }catch{testHistory.Remove(snapshot.Profile.Id);Message="测试未完成，请检查错误后重试。";throw;}
        finally{testingId=null;testMessageId=snapshot.Profile.Id;UpdateTestFeedback();}
    }
    public Task ApplySelected(bool launch)=>Run(async()=>{
        if(selected==null || Dirty)throw new IOException("请先保存配置，再应用配置或启动 ChatGPT/Codex。");var p=Build();string pass=Password;
        if(launch && Engine.Status.Running && activeId==p.Id && appliedFingerprint==ConfigurationFingerprint(p,pass)){await OpenApplication();return;}
        if(!await Test(new(p,pass,0)))return;
        if(Engine.Status.Running && Engine.Status.Port!=Data.Settings.LocalPort)throw new IOException("本地端口已更改，请停止桥接并重新启动ChatGPT/Codex应用。");
        Engine.Apply(p,pass);activeId=p.Id;Engine.Start(Data.Settings.LocalPort);appliedFingerprint=ConfigurationFingerprint(p,pass);
        Message="新连接已使用 "+p.Name+"；已有连接继续使用原配置。";
        if(launch)await OpenApplication();
    });
    async Task OpenApplication()
    {
        await Launcher.Launch(Data.Settings,Engine.Status.Running?Engine.Status.Port:Data.Settings.LocalPort);
        Message=Launcher.LastReusedExisting
            ?"已打开 ChatGPT/Codex 窗口。后台实例原有网络设置保持不变；若首次接入代理，需完全退出 ChatGPT/Codex应用后重新启动。"
            :"已通过当前代理打开 ChatGPT/Codex。";
    }
    public async Task ApplyProfile(ProxyProfile p){if(Busy)return;Selected=p;if(Selected?.Id==p.Id)await ApplySelected(false);}
    public bool Stop(){if(Engine.Status.Connections>0 && MessageBox.Show("停止代理会中断现有网络连接，但不会关闭 ChatGPT/Codex应用。继续？","停止代理",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return false;Engine.Stop();Message="代理已停止。";Refresh();return true;}
    void SaveSettings(){if(!int.TryParse(LocalPort,out int p)||p is <1 or >65535)throw new IOException("本地端口必须为 1–65535。");if(Bypass.Contains('\r')||Bypass.Contains('\n'))throw new IOException("绕过列表必须在一行内，以逗号分隔。");Data.Settings.LocalPort=p;Data.Settings.Bypass=Bypass.Trim();Data.Settings.AppPath=AppPath.Trim();Data.Settings.Theme=Theme;Persist();LocalPort=p.ToString();Bypass=Data.Settings.Bypass;AppPath=Data.Settings.AppPath;SettingsChanged();((App)Application.Current).SetTheme(Theme);Message="设置已保存。端口或绕过列表变更需退出并重新启动ChatGPT/Codex应用。";}
    async Task Import(){var dialog=new Microsoft.Win32.OpenFileDialog{Filter="旧启动脚本|*.ps1",FileName="Start-Codex-Proxy-Remote-Bridge.ps1",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)};if(dialog.ShowDialog()!=true)return;var(p,pass)=await Launcher.Import(dialog.FileName);p=p with{CredentialReference=store.SavePassword(p.Id,pass)};Profiles.Add(p);SelectWithoutPrompt(p);SettingsVisible=false;Data.Settings.DefaultProfileId??=p.Id;Persist();Message="旧配置已导入并加密保存；原脚本未改动。";}
    public void Refresh(){appOwned=Launcher.OwnedRunning;appRunning=appOwned||Launcher.HasRunning(Data.Settings.AppPath);Notify(nameof(RuntimeText));Notify(nameof(IsBridgeRunning));Notify(nameof(ActiveText));Notify(nameof(IsAppRunning));Notify(nameof(AppText));Notify(nameof(AppSourceText));Notify(nameof(ActiveProfileId));NotifyProfileState();}
}
