using Microsoft.Win32;
using System.Windows;

namespace PolinMegatranscriber.App;

internal enum AppAppearance
{
    System,
    Light,
    Dark,
}

internal static class ThemeManager
{
    internal static void Apply(AppAppearance appearance)
    {
        bool dark = appearance == AppAppearance.Dark
            || appearance == AppAppearance.System && SystemUsesDarkTheme();
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                dark ? "Themes/Dark.xaml" : "Themes/Light.xaml",
                UriKind.Relative),
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        ResourceDictionary? current = dictionaries.FirstOrDefault(
            item => item.Source?.OriginalString.StartsWith(
                "Themes/",
                StringComparison.OrdinalIgnoreCase) == true);
        if (current is null)
        {
            dictionaries.Insert(0, dictionary);
            return;
        }

        int index = dictionaries.IndexOf(current);
        dictionaries[index] = dictionary;
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int setting && setting == 0;
        }
        catch
        {
            return false;
        }
    }
}
