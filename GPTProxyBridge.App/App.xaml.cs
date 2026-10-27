using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace GPTProxyBridge.App;
public partial class App : Application
{
    Mutex? mutex; EventWaitHandle? wake; Forms.NotifyIcon? tray; MainViewModel? vm;
    public bool Exiting{get;private set;}
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex=new Mutex(true,"Local\\GPTProxyBridge.Instance",out bool first);
        wake=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\GPTProxyBridge.Wake");
        if(!first){wake.Set();Shutdown();return;}
        try {
            string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GPTProxyBridge");
            // Dedicated QA profile directory avoids changing user settings during UI tests.
            int index=Array.IndexOf(e.Args,"--data-dir");if(index>=0 && index+1<e.Args.Length)directory=Path.GetFullPath(e.Args[index+1]);
            vm=new MainViewModel(directory);SetTheme(vm.Theme);MainWindow=new MainWindow(vm);MainWindow.Show();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};timer.Tick+=(_,_)=>{if(wake.WaitOne(0))ShowWindow();};timer.Start();
            tray=new Forms.NotifyIcon{Text="GPT Proxy Bridge",Icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??System.Drawing.SystemIcons.Application,Visible=true};
            tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(ShowWindow);
            var menu=new Forms.ContextMenuStrip();menu.Opening+=(_,_)=>{
                menu.Items.Clear();menu.Items.Add("打开 GPT Proxy Bridge",null,(_,_)=>Dispatcher.Invoke(ShowWindow));
                var profiles=new Forms.ToolStripMenuItem("切换配置");foreach(var p in vm.Profiles){var item=new Forms.ToolStripMenuItem(p.Name){Enabled=!vm.Busy};item.Click+=async(_,_)=>{try{await vm.ApplyProfile(p);}catch(Exception ex){MessageBox.Show(ex.Message,"GPT Proxy Bridge");}};profiles.DropDownItems.Add(item);}menu.Items.Add(profiles);
                menu.Items.Add("连接并启动 ChatGPT/Codex",null,(_,_)=>Dispatcher.Invoke(()=>vm.LaunchCommand.Execute(null)));menu.Items.Add("打开 ChatGPT/Codex",null,(_,_)=>Dispatcher.Invoke(()=>vm.OpenAppCommand.Execute(null)));menu.Items.Add("停止代理",null,(_,_)=>Dispatcher.Invoke(()=>vm.StopCommand.Execute(null)));menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("退出",null,(_,_)=>Dispatcher.Invoke(ExitApp));
            };tray.ContextMenuStrip=menu;
            SystemEvents.UserPreferenceChanged+=(_,_)=>Dispatcher.InvokeAsync(()=>SetTheme(vm.Theme));
            if(vm.Profiles.Count==0)vm.Message="直接填写代理信息后保存，或点击“导入旧脚本配置”选择已有脚本。";
        } catch(Exception ex){MessageBox.Show(ex.Message,"GPT Proxy Bridge 启动失败");Shutdown(1);}
    }
    void ShowWindow(){MainWindow.Show();MainWindow.WindowState=WindowState.Normal;MainWindow.Activate();}
    void ExitApp(){if(vm?.Busy==true){MessageBox.Show("连接操作正在进行，请稍候再退出。","GPT Proxy Bridge");return;}if(vm!=null&&(!vm.PrepareExit()||!vm.Stop()))return;Exiting=true;tray?.Dispose();Shutdown();}
    protected override void OnExit(ExitEventArgs e){tray?.Dispose();vm?.Engine.Dispose();wake?.Dispose();mutex?.Dispose();base.OnExit(e);}
    public void SetTheme(string choice)
    {
        bool dark=choice=="Dark" || choice=="System" && (int?)(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1))==0;
        var colors=dark?new[]{"#1C1C1F","#242427","#2C2C30","#F1F1F4","#AFB0BA","#45454D","#3785F7"}:new[]{"#F6F6F8","#ECECF0","#FFFFFF","#202126","#626570","#DCDDE3","#146EF5"};
        string[] keys=["BackgroundBrush","SidebarBrush","CardBrush","TextBrush","MutedBrush","LineBrush","AccentBrush"];
        for(int i=0;i<keys.Length;i++)Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Resources["SuccessBrush"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#62D98C":"#17733A"));
        Resources["WarningBrush"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#F4C36B":"#8A5A00"));
        Resources["PrimaryBrush"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString("#145FCB"));
        Resources["PrimaryTextBrush"]=System.Windows.Media.Brushes.White;
        if(SystemParameters.HighContrast){Resources["BackgroundBrush"]=SystemColors.WindowBrush;Resources["SidebarBrush"]=SystemColors.WindowBrush;Resources["CardBrush"]=SystemColors.WindowBrush;Resources["TextBrush"]=SystemColors.WindowTextBrush;Resources["MutedBrush"]=SystemColors.WindowTextBrush;Resources["LineBrush"]=SystemColors.WindowTextBrush;Resources["AccentBrush"]=SystemColors.HighlightBrush;Resources["PrimaryBrush"]=SystemColors.HighlightBrush;Resources["PrimaryTextBrush"]=SystemColors.HighlightTextBrush;Resources["SuccessBrush"]=SystemColors.WindowTextBrush;Resources["WarningBrush"]=SystemColors.WindowTextBrush;}
    }
}
