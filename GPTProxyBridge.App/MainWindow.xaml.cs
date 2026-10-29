using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GPTProxyBridge.App;
public partial class MainWindow : Window
{
    readonly MainViewModel vm;
    bool syncing;
    public MainWindow(MainViewModel model)
    {
        vm=model;InitializeComponent();DataContext=model;SecretBox.Password=vm.Password;
        LayoutUpdated+=AlignProfilesToDivider;
        vm.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(vm.Password)&&SecretBox.Password!=vm.Password){syncing=true;SecretBox.Password=vm.Password;syncing=false;}};
        var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};timer.Tick+=(_,_)=>vm.Refresh();timer.Start();
    }
    void AlignProfilesToDivider(object? sender,EventArgs e)
    {
        if(!IsLoaded || RuntimeDivider.ActualHeight<=0 || ProfilesList.ActualHeight<=0)return;
        var dividerY=RuntimeDivider.TranslatePoint(new Point(),this).Y;
        var listY=ProfilesList.TranslatePoint(new Point(),this).Y;
        var margin=ProfilesList.Margin;
        double desired=Math.Max(0,margin.Top+dividerY-listY);
        if(Math.Abs(desired-margin.Top)>0.25)ProfilesList.Margin=new Thickness(margin.Left,desired,margin.Right,margin.Bottom);
    }
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if(Application.Current is App app)ApplyTitleBarTheme(app.IsDarkTheme);
    }
    internal void ApplyTitleBarTheme(bool dark)
    {
        var handle=new WindowInteropHelper(this).Handle;
        if(handle==IntPtr.Zero)return;
        int mode=dark?1:0;
        // Attributes 35/36 provide explicit title colors on Windows 11;
        // older Windows versions ignore unsupported attributes.
        int caption=dark?0x00252221:unchecked((int)0xFFFFFFFF);
        int text=dark?0x00F4F2F1:unchecked((int)0xFFFFFFFF);
        DwmSetWindowAttribute(handle,20,ref mode,sizeof(int));
        DwmSetWindowAttribute(handle,35,ref caption,sizeof(int));
        DwmSetWindowAttribute(handle,36,ref text,sizeof(int));
    }
    protected override void OnClosing(CancelEventArgs e){if(!((App)Application.Current).Exiting){e.Cancel=true;Hide();}base.OnClosing(e);}
    void PasswordChanged(object sender,RoutedEventArgs e){if(!syncing)vm.Password=SecretBox.Password;}
    void ShowPassword(object sender,RoutedEventArgs e){SecretBox.Visibility=Visibility.Collapsed;PlainSecret.Visibility=Visibility.Visible;}
    void HidePassword(object sender,RoutedEventArgs e){if(SecretBox==null)return;SecretBox.Visibility=Visibility.Visible;PlainSecret.Visibility=Visibility.Collapsed;}
    void ProfileClicked(object sender,System.Windows.Input.MouseButtonEventArgs e)
    {if(sender is ListBoxItem {IsSelected:true,DataContext:GPTProxyBridge.Core.ProxyProfile profile} && vm.Selected?.Id==profile.Id)vm.SettingsVisible=false;}
    void ProfileKeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {if(e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space && sender is ListBoxItem {IsSelected:true})vm.SettingsVisible=false;}
    void AppearanceSelected(object sender,RoutedEventArgs e){AppearanceToggle.IsChecked=false;}
    void BrowseApp(object sender,RoutedEventArgs e){var dialog=new Microsoft.Win32.OpenFileDialog{Title="选择 ChatGPT/Codex应用",Filter="ChatGPT/Codex可执行文件|*.exe"};if(dialog.ShowDialog()==true){vm.AppPath=dialog.FileName;}}
}
