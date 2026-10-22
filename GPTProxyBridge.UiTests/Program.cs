using System.Linq;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GPTProxyBridge.App;
using GPTProxyBridge.Core;

internal static class Program
{
    [STAThread]
    static int Main()
    {
        var app=new TestApp {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("AppStyles.xml")!) {
            var doc=System.Xml.Linq.XDocument.Load(source);
            var elements=doc.Root!.Elements().First().Elements();
            app.Resources=(ResourceDictionary)System.Windows.Markup.XamlReader.Parse("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">"+string.Concat(elements)+"</ResourceDictionary>");
        }
        var directory=Path.Combine(Path.GetTempPath(),"GPTProxyBridge-ui-"+Guid.NewGuid());
        var vm=new MainViewModel(directory);
        var window=new MainWindow(vm);
        try {
            window.Show();Pump();
            Check(vm.Selected==null,"empty startup has no saved profile");
            vm.ProfileName="直接填写";vm.Host="127.0.0.1";vm.Port="18888";
            vm.Save();Pump();
            var id=vm.Selected!.Id;
            Check(vm.Profiles.Count==1 && vm.Selected.Name=="直接填写","save from empty editor creates and selects profile");
            vm.Save();Pump();
            Check(vm.Selected?.Id==id && vm.Profiles.Count==1,"second save preserves selection and identity");
            vm.ProfileName="修改后的配置";vm.Save();Pump();
            Check(vm.Selected?.Id==id && vm.Selected.Name=="修改后的配置","edit/save keeps ListBox selection");
            for(int i=0;i<10;i++){vm.Save();Pump();}
            var built=(ProxyProfile)typeof(MainViewModel).GetMethod("Build",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(vm,null)!;
            Check(built.Id==id && !vm.Dirty,"saved profile remains available to apply/launch path");
            vm.Authentication=true;vm.Username="test-user";vm.Password="test-secret";vm.Save();Pump();vm.Save();Pump();
            Check(vm.Selected?.Id==id && vm.Password=="test-secret","authenticated repeated saves preserve password and selection");
            vm.Theme="Dark";Pump();vm.Busy=true;Pump();
            var list=Find<ListBox>(window)!;list.ApplyTemplate();
            var border=System.Windows.Media.VisualTreeHelper.GetChild(list,0) as Border;
            Check(border?.Background.ToString()==app.Resources["SidebarBrush"].ToString(),"disabled profile list retains dark sidebar background");
            var editor=(ScrollViewer)window.FindName("EditorScroll");
            Check(editor.ScrollableHeight<1,"authenticated editor fits without scrolling");
            vm.Results.Add("通过 · 地址解析：解析成功");vm.Results.Add("通过 · 代理连接：认证成功");vm.Results.Add("通过 · HTTPS：收到 HTTP 401");
            window.Width=1000;window.Height=780;Pump();
            Console.WriteLine($"Layout: scroll={editor.ScrollableHeight}, viewport={editor.ViewportHeight}, extent={editor.ExtentHeight}");Check(editor.ScrollableHeight<1,"authenticated editor fits minimum window without scrolling");
            window.Width=1060;window.Height=800;Pump();
            var preview=Environment.GetEnvironmentVariable("GPT_PB_PREVIEW");
            if(!string.IsNullOrEmpty(preview)) {
                var root=(FrameworkElement)window.Content;root.UpdateLayout();
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file=File.Create(preview);encoder.Save(file);
            }
            vm.Busy=false;vm.Theme="Light";Pump();
            Check(vm.Data.Settings.Theme=="Light" && new ConfigurationStore(directory).Load().Settings.Theme=="Light","sidebar theme persists immediately");
            Check(vm.OpenAppCommand!=null,"independent open application command exists");
            vm.AddCommand.Execute(null);Pump();
            Check(vm.Selected==null && vm.Profiles.Count==1,"new button opens draft without adding an incomplete saved profile");
            vm.ProfileName="第二个配置";vm.Save();Pump();
            Check(vm.Profiles.Count==2 && vm.Selected?.Id!=id,"new draft saves as separate selected profile");
            vm.SettingsCommand.Execute(null);Pump();vm.SettingsCommand.Execute(null);Pump();
            Check(vm.SettingsVisible,"settings navigation remains on settings when clicked twice");
            list.UpdateLayout();list.ScrollIntoView(vm.Selected);window.UpdateLayout();Pump();
            var selectedItem=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vm.Selected!);
            selectedItem.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.UIElement.PreviewMouseLeftButtonUpEvent});Pump();
            Check(vm.EditorVisible,"clicking already selected profile returns from settings");
            vm.SettingsCommand.Execute(null);Pump();list.SelectedItem=vm.Profiles[0];Pump();
            Check(vm.EditorVisible && vm.Selected?.Id==id,"selecting another profile returns from settings");
            var portBox=FindNamed(window,"代理端口")!;
            var timeoutBox=FindNamed(window,"连接超时")!;
            Check(Math.Abs(portBox.TranslatePoint(new Point(),window).X-timeoutBox.TranslatePoint(new Point(),window).X)<1 && Math.Abs(portBox.ActualWidth-timeoutBox.ActualWidth)<1,"port and timeout input edges align");
            var fixture=Environment.GetEnvironmentVariable("GPT_PB_FIXTURE_EXE");
            if(string.IsNullOrEmpty(fixture))throw new Exception("Set GPT_PB_FIXTURE_EXE to the built launch fixture.");
            var fixtureDirectory=Path.Combine(directory,"launch");
            Environment.SetEnvironmentVariable("GPT_PB_LAUNCH_TEST_DIR",fixtureDirectory);
            var launcher=new Launcher();
            try {
                Task.Run(()=>launcher.Launch(new AppSettings{AppPath=fixture},7995)).GetAwaiter().GetResult();
                Check(launcher.OwnedRunning&&!launcher.LastReusedExisting,"fully exited desktop can be launched with bridge remaining active");
                Task.Run(()=>launcher.Launch(new AppSettings{AppPath=fixture},7995)).GetAwaiter().GetResult();
                Check(launcher.LastReusedExisting&&File.Exists(Path.Combine(fixtureDirectory,"reopened")),"windowless background instance receives reopen request instead of running error");
            } finally {if(launcher.OwnedRunning)launcher.OwnedProcess!.Kill();Environment.SetEnvironmentVariable("GPT_PB_LAUNCH_TEST_DIR",null);}
            Console.WriteLine("RESULT: 19 UI / launch regression checks passed");
            return 0;
        } catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally {window.Hide();vm.Engine.Dispose();app.Shutdown();}
    }
    static T? Find<T>(DependencyObject root) where T:DependencyObject {
        if(root is T match)return match;
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(System.Windows.Media.VisualTreeHelper.GetChild(root,i)) is { } item)return item;
        return null;
    }
    static FrameworkElement? FindNamed(DependencyObject root,string name) {
        if(root is FrameworkElement f && System.Windows.Automation.AutomationProperties.GetName(f)==name)return f;
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
            if(FindNamed(System.Windows.Media.VisualTreeHelper.GetChild(root,i),name) is {} found)return found;
        return null;
    }
    static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);foreach(Window window in Application.Current.Windows)window.UpdateLayout();}
    static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);}
}



internal sealed class TestApp : App { protected override void OnStartup(StartupEventArgs e) {} }
