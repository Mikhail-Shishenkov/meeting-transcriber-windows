using System.Globalization;
using System.Windows;

namespace PolinMegatranscriber.App;

internal enum UiLanguage
{
    Russian,
    English,
    Italian,
}

internal static class LocalizationManager
{
    private const string ResourcePrefix =
        "Resources/Localization/Strings.";

    internal static event EventHandler? LanguageChanged;

    internal static UiLanguage CurrentLanguage { get; private set; } =
        UiLanguage.English;

    internal static UiLanguage SystemDefault
    {
        get
        {
            string name = CultureInfo.CurrentUICulture.Name;
            if (name.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            {
                return UiLanguage.Russian;
            }

            return name.StartsWith("it", StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.Italian
                : UiLanguage.English;
        }
    }

    internal static void Apply(UiLanguage language)
    {
        string code = ToCode(language);
        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                $"{ResourcePrefix}{code}.xaml",
                UriKind.Relative),
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        ResourceDictionary? current = dictionaries.FirstOrDefault(
            item => item.Source?.OriginalString.StartsWith(
                ResourcePrefix,
                StringComparison.OrdinalIgnoreCase) == true);
        if (current is null)
        {
            dictionaries.Add(replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(current)] = replacement;
        }

        CurrentLanguage = language;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    internal static string Get(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;

    internal static string ToCode(UiLanguage language) => language switch
    {
        UiLanguage.Russian => "ru",
        UiLanguage.Italian => "it",
        _ => "en",
    };

    internal static UiLanguage FromCode(string? code, UiLanguage fallback) =>
        code?.ToLowerInvariant() switch
        {
            "ru" => UiLanguage.Russian,
            "en" => UiLanguage.English,
            "it" => UiLanguage.Italian,
            _ => fallback,
        };
}
