using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GPTProxyBridge.App;
public partial class MainWindow : Window
{
    readonly MainViewModel vm;
    bool syncing;
    public MainWindow(MainViewModel model)
    {
        vm=model;InitializeComponent();DataContext=model;SecretBox.Password=vm.Password;
        vm.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(vm.Password)&&SecretBox.Password!=vm.Password){syncing=true;SecretBox.Password=vm.Password;syncing=false;}};
        var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};timer.Tick+=(_,_)=>vm.Refresh();timer.Start();
    }
    protected override void OnClosing(CancelEventArgs e){if(!((App)Application.Current).Exiting){e.Cancel=true;Hide();}base.OnClosing(e);}
    void PasswordChanged(object sender,RoutedEventArgs e){if(!syncing)vm.Password=SecretBox.Password;}
    void ShowPassword(object sender,RoutedEventArgs e){SecretBox.Visibility=Visibility.Collapsed;PlainSecret.Visibility=Visibility.Visible;}
    void HidePassword(object sender,RoutedEventArgs e){if(SecretBox==null)return;SecretBox.Visibility=Visibility.Visible;PlainSecret.Visibility=Visibility.Collapsed;}
    void ProfileClicked(object sender,System.Windows.Input.MouseButtonEventArgs e)
    {if(sender is ListBoxItem {IsSelected:true})vm.SettingsVisible=false;}
    void ProfileKeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {if(e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space && sender is ListBoxItem {IsSelected:true})vm.SettingsVisible=false;}
    void BrowseApp(object sender,RoutedEventArgs e){var dialog=new Microsoft.Win32.OpenFileDialog{Title="选择 ChatGPT/Codex应用",Filter="ChatGPT/Codex可执行文件|*.exe"};if(dialog.ShowDialog()==true){vm.AppPath=dialog.FileName;DataContext=null;DataContext=vm;}}
}
