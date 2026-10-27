using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GPTProxyBridge.App;
public sealed class ThemeModeConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>Equals(value,parameter);
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>value is true ? parameter : Binding.DoNothing;
}
public sealed class ThemeLabel : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value?.ToString() switch {"System"=>"跟随系统","Light"=>"浅色","Dark"=>"深色",_=>value??""};
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
public sealed class SidebarBottomMargin : IMultiValueConverter
{
    public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)
    {
        if(values.Length!=3 || values[0] is not double barHeight || values[1] is not double themeHeight || values[2] is not double footerHeight)return new Thickness(0);
        // Account for the theme gap, footer gap, and sidebar's bottom inset.
        return new Thickness(0,0,0,Math.Max(0,barHeight-(8+themeHeight+14+footerHeight+18)));
    }
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
public sealed class ProfileStateLabel : IMultiValueConverter
{
    public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)
    {
        if(values.Length!=3 || values[0] is not Guid id)return "";
        bool active=values[1] is Guid a&&a==id,defaultProfile=values[2] is Guid d&&d==id;
        return active&&defaultProfile?"正在使用 · 默认":active?"正在使用":defaultProfile?"默认配置":"";
    }
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
