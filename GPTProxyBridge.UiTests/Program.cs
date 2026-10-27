using System.Linq;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GPTProxyBridge.App;
using GPTProxyBridge.Core;

internal static partial class Program
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
        var vm=new MainViewModel(directory,probe:_=>Task.FromResult(new ProbeResult(true,new[]{new ProbeStep("地址解析",true,"本机模拟结果"),new ProbeStep("代理连接",true,"模拟连接成功"),new ProbeStep("目标 HTTPS",true,"模拟 HTTP 401")})));
        var window=new MainWindow(vm);
        try {
            window.Show();Pump();Capture(window,"01-empty");
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

            vm.Theme="Light";Pump();Capture(window,"02-profile-light");
            var brand=(TextBlock)window.FindName("BrandTitle");
            var bridgeTitle=(TextBlock)window.FindName("BridgeSectionTitle");
            var appTitle=(TextBlock)window.FindName("AppSectionTitle");
            var runtimeBar=(Border)window.FindName("RuntimeBar");
            var profilesList=(ListBox)window.FindName("ProfilesList");
            var firstProfile=(ListBoxItem)profilesList.ItemContainerGenerator.ContainerFromIndex(0);
            var brandTop=brand.TranslatePoint(new Point(),window).Y;
            var titleTop=bridgeTitle.TranslatePoint(new Point(),window).Y;
            Check(titleTop>=brandTop && titleTop<brandTop+brand.ActualHeight
                && Math.Abs(appTitle.TranslatePoint(new Point(),window).Y-titleTop)<1,
                "status section titles align within brand title height");
            Check(Math.Abs(runtimeBar.TranslatePoint(new Point(0,runtimeBar.ActualHeight-runtimeBar.BorderThickness.Bottom),window).Y
                -firstProfile.TranslatePoint(new Point(),window).Y)<1,
                "status divider aligns with first profile top edge");
            var addressHelp=(Button)window.FindName("AddressHelp");
            var authHelp=(Button)window.FindName("AuthenticationHelp");
            Check(addressHelp.IsVisible && authHelp.IsVisible && addressHelp.ActualWidth==20 && authHelp.ActualWidth==20
                && Equals(addressHelp.ToolTip,"支持本机、局域网、公网地址及域名")
                && Equals(authHelp.ToolTip,"HTTP代理认证未加密，请勿在不安全的网络中使用。\n密码仅为当前 Windows 用户加密保存。")
                && ToolTipService.GetShowsToolTipOnKeyboardFocus(addressHelp)==true
                && ToolTipService.GetShowsToolTipOnKeyboardFocus(authHelp)==true,"compact help controls expose correct mouse and keyboard tooltips");
            var shortActions=All<Button>(window).Where(b=>b.Command==vm.SaveCommand||b.Command==vm.DuplicateCommand||b.Command==vm.DefaultCommand||b.Command==vm.DeleteCommand||b.Command==vm.TestCommand||b.Command==vm.ApplyCommand||b.Command==vm.StopCommand).ToArray();
            Check(shortActions.Length==7 && shortActions.All(b=>b.ActualWidth==84 && b.ActualHeight==36),"profile and connection short actions use the same dimensions");
            var save=shortActions.Single(b=>b.Command==vm.SaveCommand);
            var test=shortActions.Single(b=>b.Command==vm.TestCommand);
            var host=FindNamed(window,"代理地址")!;
            var profileName=FindNamed(window,"配置名称")!;
            var profileCard=(Border)window.FindName("BasicProfileCard");
            Check(Math.Abs(save.TranslatePoint(new Point(),window).X-test.TranslatePoint(new Point(),window).X)<1
                && Math.Abs(save.TranslatePoint(new Point(),window).X-profileCard.TranslatePoint(new Point(),window).X)<1
                && host.ActualHeight==36 && profileName.ActualHeight==36,"both action rows align with card edge and controls retain their height");
            var combo=All<ComboBox>(window).Single();combo.ApplyTemplate();
            var toggle=All<System.Windows.Controls.Primitives.ToggleButton>(combo).First();toggle.ApplyTemplate();
            var box=(Border)toggle.Template.FindName("Box",toggle);
            var hostBox=(TextBox)host;hostBox.ApplyTemplate();
            var inputBorder=(Border)hostBox.Template.FindName("InputBorder",hostBox);
            var passwordInput=(PasswordBox)window.FindName("SecretBox");passwordInput.ApplyTemplate();
            var passwordBorder=(Border)passwordInput.Template.FindName("InputBorder",passwordInput);
            Check(inputBorder.CornerRadius==box.CornerRadius && passwordBorder.CornerRadius==box.CornerRadius
                && hostBox.Template.FindName("PART_ContentHost",hostBox)!=null && passwordInput.Template.FindName("PART_ContentHost",passwordInput)!=null,
                "text and password inputs match protocol corners and retain editing hosts");
            var textHost=(ScrollViewer)hostBox.Template.FindName("PART_ContentHost",hostBox);
            var passwordHost=(ScrollViewer)passwordInput.Template.FindName("PART_ContentHost",passwordInput);
            Check(textHost.ActualHeight>=30 && passwordHost.ActualHeight>=30,"text and password editing regions retain sufficient height");
            var originalBorder=combo.BorderBrush;combo.BorderBrush=System.Windows.Media.Brushes.Magenta;Pump();
            Check(box.BorderBrush==System.Windows.Media.Brushes.Magenta,"protocol dropdown visible border follows focus brush");
            combo.BorderBrush=originalBorder;Pump();
            Check(All<TextBlock>(combo).Any(t=>t.Text=="⌄" && t.Foreground.ToString()==app.Resources["TextBrush"].ToString()),"protocol dropdown arrow follows theme text color");
            var eye=(System.Windows.Controls.Primitives.ToggleButton)window.FindName("PasswordReveal");
            var masked=(PasswordBox)window.FindName("SecretBox");
            var plain=(TextBox)window.FindName("PlainSecret");
            eye.IsChecked=true;Pump();
            Check(plain.IsVisible && !masked.IsVisible && plain.Text=="test-secret","eye reveals password without changing value");
            plain.Text="edited-test-secret";Pump();eye.IsChecked=false;Pump();
            Check(masked.IsVisible && !plain.IsVisible && masked.Password=="edited-test-secret" && vm.Password=="edited-test-secret","eye hides edited password without losing it");
            var field=(FrameworkElement)window.FindName("PasswordField");
            var eyeOffset=eye.TranslatePoint(new Point(),field);
            Check(eyeOffset.X>0 && eyeOffset.X+eye.ActualWidth<=field.ActualWidth && masked.Padding.Right>=eye.ActualWidth,"eye stays inside password field with text clearance");
            vm.Password="test-secret";vm.Save();Pump();
            var modes=All<RadioButton>(window).Where(b=>b.GroupName=="SidebarAppearance").ToArray();
            Check(modes.Length==3 && modes.All(b=>b.ToolTip!=null),"three appearance icon controls provide hover labels");
            foreach(var mode in new[]{"Light","Dark","System"}) {
                modes.Single(b=>Equals(b.Tag,mode)).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);Pump();
                Check(vm.Theme==mode && new ConfigurationStore(directory).Load().Settings.Theme==mode
                    && All<RadioButton>(window).Count(b=>Equals(b.Tag,mode)&&b.IsChecked==true)==2,"appearance "+mode+" persists and synchronizes both locations");
            }
            var runtime=(TextBlock)window.FindName("RuntimeLabel");
            vm.Busy=false;vm.Refresh();Pump();
            Check(runtime.Foreground.ToString()==app.Resources["MutedBrush"].ToString(),"stopped runtime uses muted status color");
            vm.Busy=true;Pump();
            Check(runtime.Foreground.ToString()==app.Resources["WarningBrush"].ToString(),"pending runtime uses amber status color");
            vm.Engine.Apply(vm.Selected!,"test-secret");vm.Engine.Start(0);vm.Refresh();Pump();
            Check(runtime.Foreground.ToString()==app.Resources["SuccessBrush"].ToString(),"running runtime stays green while operation is busy");
            vm.Busy=false;
            foreach(var mode in new[]{"Light","Dark"}) {
                vm.Theme=mode;Pump();
                Check(runtime.Foreground.ToString()==app.Resources["SuccessBrush"].ToString(),"running status follows "+mode+" palette");
            }
            vm.Engine.Stop();vm.Refresh();Pump();
            Check(runtime.Foreground.ToString()==app.Resources["MutedBrush"].ToString(),"stopping resets status color");

            vm.TestCommand.Execute(null);Pump();
            vm.Theme="Dark";Pump();vm.Busy=true;Pump();
            var list=Find<ListBox>(window)!;list.ApplyTemplate();
            var border=System.Windows.Media.VisualTreeHelper.GetChild(list,0) as Border;
            Check(border?.Background.ToString()==app.Resources["SidebarBrush"].ToString(),"disabled profile list retains dark sidebar background");
            var editor=(ScrollViewer)window.FindName("EditorScroll");
            Check(editor.ScrollableHeight<1,"authenticated editor fits without scrolling");

            window.Width=1000;window.Height=780;Pump();
            Console.WriteLine($"Layout: scroll={editor.ScrollableHeight}, viewport={editor.ViewportHeight}, extent={editor.ExtentHeight}");Check(editor.ScrollableHeight<1,"authenticated editor fits minimum window without scrolling");
            window.Width=1060;window.Height=800;Pump();
            vm.Busy=false;vm.Data.Settings.LocalPort=0;vm.ApplySelected(false).GetAwaiter().GetResult();Pump();
            var preview=Environment.GetEnvironmentVariable("GPT_PB_PREVIEW");
            if(!string.IsNullOrEmpty(preview)) {
                var root=(FrameworkElement)window.Content;root.UpdateLayout();
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file=File.Create(preview);encoder.Save(file);
            }
            Capture(window,"03-running-dark");
            vm.Engine.Stop();vm.Refresh();vm.Busy=false;vm.Theme="Light";Pump();
            Check(vm.Data.Settings.Theme=="Light" && new ConfigurationStore(directory).Load().Settings.Theme=="Light","sidebar theme persists immediately");
            Check(vm.OpenAppCommand!=null,"independent open application command exists");
            vm.AddCommand.Execute(null);Pump();
            Check(vm.Selected==null && vm.Profiles.Count==1,"new button opens draft without adding an incomplete saved profile");
            vm.ProfileName="第二个配置";vm.Save();Pump();
            Check(vm.Profiles.Count==2 && vm.Selected?.Id!=id,"new draft saves as separate selected profile");
            vm.SettingsCommand.Execute(null);Pump();vm.SettingsCommand.Execute(null);Pump();
            Check(vm.SettingsVisible,"settings navigation remains on settings when clicked twice");Capture(window,"04-settings-light");
            list.UpdateLayout();list.ScrollIntoView(vm.Selected);window.UpdateLayout();Pump();
            var selectedItem=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vm.Selected!);
            selectedItem.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.UIElement.PreviewMouseLeftButtonUpEvent});Pump();
            Check(vm.EditorVisible,"clicking already selected profile returns from settings");
            vm.SettingsCommand.Execute(null);Pump();list.SelectedItem=vm.Profiles[0];Pump();
            Check(vm.EditorVisible && vm.Selected?.Id==id,"selecting another profile returns from settings");
            var portBox=FindNamed(window,"代理端口")!;
            var timeoutBox=FindNamed(window,"连接超时")!;
            Check(Math.Abs(portBox.TranslatePoint(new Point(),window).X-timeoutBox.TranslatePoint(new Point(),window).X)<1 && Math.Abs(portBox.ActualWidth-timeoutBox.ActualWidth)<1,"port and timeout input edges align");
            StateRegression(directory);
            UpdateRegression(directory);
            var fixture=Environment.GetEnvironmentVariable("GPT_PB_FIXTURE_EXE");
            if(string.IsNullOrEmpty(fixture))throw new Exception("Set GPT_PB_FIXTURE_EXE to the built launch fixture.");
            var fixtureDirectory=Path.Combine(directory,"launch");
            Environment.SetEnvironmentVariable("GPT_PB_LAUNCH_TEST_DIR",fixtureDirectory);
            var launcher=vm.Launcher;
            vm.Data.Settings.AppPath=fixture;
            var externalVm=new MainViewModel(Path.Combine(directory,"external-app-status"));
            externalVm.Data.Settings.AppPath=fixture;
            try {
                Task.Run(()=>launcher.Launch(new AppSettings{AppPath=fixture},7995)).GetAwaiter().GetResult();
                Check(launcher.OwnedRunning&&!launcher.LastReusedExisting,"fully exited desktop can be launched with bridge remaining active");
                vm.Refresh();Pump();
                var appRuntime=(TextBlock)window.FindName("AppRuntimeLabel");
                var appSource=(TextBlock)window.FindName("AppSourceInfo");
                var activeInfo=(TextBlock)window.FindName("ActiveProfileInfo");
                Check(vm.IsAppRunning && vm.AppText=="ChatGPT/Codex应用已运行" && vm.AppSourceText=="由本软件启动"
                    && appRuntime.Foreground.ToString()==app.Resources["SuccessBrush"].ToString(),"owned application status is green with separate source text");
                Check(appSource.FontSize==activeInfo.FontSize && appSource.Foreground.ToString()==activeInfo.Foreground.ToString()
                    && Math.Abs(appSource.TranslatePoint(new Point(),window).Y-activeInfo.TranslatePoint(new Point(),window).Y)<1,
                    "application source matches active-profile font, color and baseline");
                externalVm.Refresh();
                Check(externalVm.IsAppRunning && externalVm.AppSourceText=="未由本软件启动","externally started application source is distinguished");
                vm.Theme="Dark";Pump();
                Check(appRuntime.Foreground.ToString()==app.Resources["SuccessBrush"].ToString(),"running application status uses dark-theme success color");
                Capture(window,"06-app-running-dark");
                vm.Theme="Light";Pump();
                Task.Run(()=>launcher.Launch(new AppSettings{AppPath=fixture},7995)).GetAwaiter().GetResult();
                Check(launcher.LastReusedExisting&&File.Exists(Path.Combine(fixtureDirectory,"reopened")),"windowless background instance receives reopen request instead of running error");
                launcher.OwnedProcess!.Kill();launcher.OwnedProcess.WaitForExit();vm.Refresh();Pump();
                Check(!vm.IsAppRunning && vm.AppText=="ChatGPT/Codex应用未运行" && vm.AppSourceText==""
                    && appRuntime.Foreground.ToString()==app.Resources["MutedBrush"].ToString(),"exited application returns to neutral status");
            } finally {if(launcher.OwnedRunning)launcher.OwnedProcess!.Kill();externalVm.Engine.Dispose();Environment.SetEnvironmentVariable("GPT_PB_LAUNCH_TEST_DIR",null);}
            Console.WriteLine($"RESULT: {passed} UI / launch regression checks passed");
            return 0;
        } catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally {window.Hide();vm.Engine.Dispose();app.Shutdown();}
    }

    static void StateRegression(string directory)
    {
        var answers=new System.Collections.Generic.Queue<MessageBoxResult>();
        int prompts=0,tests=0;bool succeeds=true;TaskCompletionSource<ProbeResult>? pending=null;
        var result=new Func<ProbeResult>(()=>new(succeeds,new[]{
            new ProbeStep("地址解析",true,"本机模拟结果"),
            new ProbeStep("代理连接",succeeds,succeeds?"模拟连接成功":"模拟认证失败"),
            new ProbeStep("目标 HTTPS",succeeds,succeeds?"模拟 HTTP 401":"未执行")}));
        var vm=new MainViewModel(Path.Combine(directory,"states"),
            _=>{prompts++;return answers.Count>0?answers.Dequeue():MessageBoxResult.Cancel;},
            _=>{tests++;return pending?.Task??Task.FromResult(result());});
        var window=new MainWindow(vm);
        try{
            window.Show();Pump();
            var list=Find<ListBox>(window)!;
            Check(!vm.DuplicateCommand.CanExecute(null)&&!vm.DefaultCommand.CanExecute(null)&&!vm.DeleteCommand.CanExecute(null)
                &&!vm.ApplyCommand.CanExecute(null)&&!vm.LaunchCommand.CanExecute(null),"empty editor disables target-dependent operations");
            Check(All<Button>(window).Where(b=>b.Command==vm.DeleteCommand||b.Command==vm.ApplyCommand).All(b=>!b.IsEnabled),"WPF buttons reflect unavailable actions");
            vm.ProfileName="已保存A";vm.Port="18888";vm.Save();Pump();var first=vm.Selected!;
            Check(vm.CanApplyProfile&&vm.HasPendingApply&&vm.ProfileStateText=="已保存，待应用","saved profile has persistent pending state");
            vm.AddCommand.Execute(null);Pump();vm.ProfileName="不能丢的草稿";vm.Host="example.com";Pump();
            answers.Enqueue(MessageBoxResult.Cancel);list.SelectedItem=first;Pump();
            Check(vm.Selected==null&&list.SelectedItem==null&&vm.ProfileName=="不能丢的草稿"&&vm.Host=="example.com","cancelled list navigation preserves draft and restores selection");
            answers.Enqueue(MessageBoxResult.Yes);list.SelectedItem=first;Pump();
            var second=vm.Profiles.Single(p=>p.Name=="不能丢的草稿");
            Check(vm.Selected?.Id==first.Id&&vm.Profiles.Count==2&&second.Host=="example.com","save-and-continue saves new draft before selecting existing profile");
            vm.AddCommand.Execute(null);Pump();vm.ProfileName="放弃的草稿";
            answers.Enqueue(MessageBoxResult.No);list.SelectedItem=first;Pump();
            Check(vm.Selected?.Id==first.Id&&vm.Profiles.Count==2,"discard-and-continue does not persist abandoned draft");
            vm.AddCommand.Execute(null);Pump();vm.ProfileName="无效草稿";vm.Port="bad";
            answers.Enqueue(MessageBoxResult.Yes);list.SelectedItem=first;Pump();
            Check(vm.Selected==null&&list.SelectedItem==null&&vm.Port=="bad"&&vm.Message.Contains("保留"),"failed save blocks navigation and retains invalid input");
            answers.Enqueue(MessageBoxResult.Cancel);vm.AddCommand.Execute(null);Pump();
            Check(vm.ProfileName=="无效草稿"&&vm.Port=="bad","new action also respects cancelled dirty draft");
            vm.Port="18889";answers.Enqueue(MessageBoxResult.Yes);vm.AddCommand.Execute(null);Pump();
            Check(vm.Profiles.Count==3&&vm.Selected==null&&vm.ProfileName=="","save-and-new saves draft then starts a separate editor");
            vm.Selected=first;Pump();
            vm.ProfileName="未保存的已有配置";answers.Enqueue(MessageBoxResult.Cancel);list.SelectedItem=second;Pump();
            Check(vm.Selected?.Id==first.Id&&vm.ProfileName=="未保存的已有配置","existing profile edits also survive cancelled navigation");
            answers.Enqueue(MessageBoxResult.No);list.SelectedItem=second;Pump();
            vm.Selected=first;Pump();
            Check(vm.ProfileName=="已保存A","discard restores saved existing profile contents");
            vm.TestCommand.Execute(null);Pump();
            Check(tests==1&&vm.Results.Count==3&&vm.HasTestRecord&&vm.TestSummaryText.Contains(first.Name),"test feedback belongs to profile and includes name/time");
            vm.Port="18890";Pump();
            Check(vm.Results.Count==0&&vm.TestStatusText.Contains("需重新测试")&&vm.Message.Contains("需重新测试"),"network edit hides obsolete passing test");
            vm.Save();Pump();
            Check(vm.Results.Count==0&&vm.TestStatusText.Contains("需重新测试"),"saving changes does not revalidate old test");
            vm.Port="18888";vm.Save();Pump();
            Check(vm.Results.Count==3,"restoring the exact tested parameters restores matching result");
            vm.Selected=second;Pump();
            Check(vm.Results.Count==0&&!vm.HasTestFeedback&&!vm.Message.StartsWith("连接测试通过"),"switching to untested profile does not reuse another result");
            vm.Selected=vm.Profiles.Single(p=>p.Id==first.Id);Pump();
            Check(vm.Results.Count==3,"returning to tested profile restores its own result");
            vm.SettingsCommand.Execute(null);Pump();
            var feedback=(FrameworkElement)window.FindName("ProfileTestFeedback");
            var nav=(Button)window.FindName("SettingsNavigation");
            var row=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vm.Selected!);row.ApplyTemplate();
            var chrome=(Border)row.Template.FindName("Row",row);
            Check(!feedback.IsVisible&&nav.Background.ToString()==Application.Current.Resources["PrimaryBrush"].ToString()
                &&chrome.BorderThickness==new Thickness(0),"settings marks current page, clears profile highlight and hides test feedback");
            Check(!vm.Message.Contains("点击“应用配置”"),"settings does not show profile-specific save guidance");
            var settingsMessage=(TextBlock)window.FindName("SettingsConnectionMessage");
            var connectionTitle=(TextBlock)window.FindName("ConnectionTitle");
            var connectionBar=(Border)window.FindName("ConnectionBar");
            var appearance=(FrameworkElement)window.FindName("AppearanceButtons");
            var importButton=All<Button>(window).Single(b=>b.Command==vm.ImportCommand);
            Check(settingsMessage.IsVisible && settingsMessage.Text==vm.Message
                && Math.Abs(settingsMessage.TranslatePoint(new Point(),window).Y-connectionTitle.TranslatePoint(new Point(),window).Y)<1,
                "settings guidance appears beside connection title");
            Check(Math.Abs(nav.TranslatePoint(new Point(0,nav.ActualHeight),window).Y-connectionBar.TranslatePoint(new Point(),window).Y)<1
                && Math.Abs((appearance.TranslatePoint(new Point(),window).Y-nav.TranslatePoint(new Point(0,nav.ActualHeight),window).Y)
                    -(nav.TranslatePoint(new Point(),window).Y-importButton.TranslatePoint(new Point(0,importButton.ActualHeight),window).Y))<1,
                "settings bottom aligns with connection divider and sidebar gaps match");
            vm.LocalPort="7996";Pump();
            Check(vm.SettingsDirty&&vm.SaveSettingsCommand.CanExecute(null),"connection setting edit shows unsaved state and enables save");
            vm.Theme="Dark";Pump();
            Check(vm.SettingsDirty&&vm.Data.Settings.LocalPort!=7996,"instant theme save does not silently save connection settings");
            vm.Selected=vm.Selected;Pump();
            Check(vm.EditorVisible&&vm.SettingsDirty&&vm.LocalPort=="7996","settings draft survives navigation back to profile");
            Check(!vm.Message.Contains("连接设置修改后请点击"),"settings-only guidance clears when returning to a profile");
            vm.SettingsCommand.Execute(null);Pump();vm.SaveSettingsCommand.Execute(null);Pump();
            Check(!vm.SettingsDirty&&!vm.SaveSettingsCommand.CanExecute(null)&&vm.Data.Settings.LocalPort==7996,"saving connection settings clears dirty marker");
            vm.Selected=vm.Selected;vm.Data.Settings.LocalPort=0;
            vm.ApplySelected(false).GetAwaiter().GetResult();Pump();
            Check(!vm.HasPendingApply&&vm.ProfileStateText.Contains("已应用")&&vm.RuntimeText.StartsWith("本地桥接运行中"),"successful apply marks saved snapshot as applied");
            Check(!vm.DeleteCommand.CanExecute(null),"active profile cannot be deleted");
            vm.Data.Settings.LocalPort=vm.Engine.Status.Port;vm.LocalPort=vm.Engine.Status.Port.ToString();
            long revision=vm.Engine.Status.Revision;
            vm.Port="18891";Pump();
            Check(!vm.ApplyCommand.CanExecute(null)&&!vm.LaunchCommand.CanExecute(null)&&vm.ConnectionHint.Contains("先保存"),"unsaved editor disables apply/launch with save guidance");
            vm.Save();Pump();vm.Message="这条普通消息覆盖了保存提示";vm.Theme="Light";Pump();
            Check(vm.HasPendingApply&&vm.ProfileStateText=="已保存，待应用","pending saved revision survives unrelated messages and theme changes");
            vm.ApplySelected(false).GetAwaiter().GetResult();Pump();
            Check(!vm.HasPendingApply&&vm.Engine.Status.Revision>revision,"reapply advances snapshot and clears pending state");
            revision=vm.Engine.Status.Revision;succeeds=false;vm.Selected=second;Pump();
            vm.ApplySelected(false).GetAwaiter().GetResult();Pump();
            Check(vm.Engine.Status.Revision==revision&&vm.ActiveProfileId==first.Id&&vm.HasPendingApply,"failed apply keeps old active snapshot and pending new selection");
            succeeds=true;pending=new TaskCompletionSource<ProbeResult>();
            vm.TestCommand.Execute(null);Pump();vm.Port="18892";pending.SetResult(result());Pump();
            Check(!vm.Busy&&vm.Results.Count==0&&vm.TestStatusText.Contains("需重新测试"),"late test completion cannot validate subsequently edited parameters");
            Check(prompts==8,"all expected draft decisions went through the same guard");
            answers.Enqueue(MessageBoxResult.Cancel);
            Check(!vm.PrepareExit()&&vm.Port=="18892","explicit exit cancellation protects current editor");
            answers.Enqueue(MessageBoxResult.Yes);
            Check(vm.PrepareExit()&&!vm.Dirty,"explicit exit can save draft without closing during test");
            vm.SettingsCommand.Execute(null);vm.LocalPort=" 07997 ";vm.Bypass=" localhost,127.0.0.1 ";
            answers.Enqueue(MessageBoxResult.Cancel);
            Check(!vm.PrepareExit()&&vm.SettingsDirty,"explicit exit cancellation protects connection settings");
            answers.Enqueue(MessageBoxResult.Yes);
            Check(vm.PrepareExit()&&!vm.SettingsDirty&&vm.LocalPort=="7997"&&vm.Bypass=="localhost,127.0.0.1","saved connection settings normalize editor and clear dirty state");
            vm.Selected=vm.Selected;vm.Port="18893";
            window.Width=1000;window.Height=780;Pump();Capture(window,"05-stale-result");
            vm.Engine.Stop();vm.Refresh();
        }finally{window.Hide();vm.Engine.Dispose();}
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
    static void Capture(Window window,string name) {
        var directory=Environment.GetEnvironmentVariable("GPT_PB_AUDIT_DIR");
        if(string.IsNullOrEmpty(directory))return;
        Directory.CreateDirectory(directory);
        var root=(FrameworkElement)window.Content;root.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(directory,name+".png"));encoder.Save(file);
    }
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static System.Collections.Generic.IEnumerable<T> All<T>(DependencyObject root) where T:DependencyObject {
        if(root is T match)yield return match;
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in All<T>(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}



internal sealed class TestApp : App { protected override void OnStartup(StartupEventArgs e) {} }
