using PolinMegatranscriber.Core;
using System.IO;
using System.Text.Json;

namespace PolinMegatranscriber.App;

internal sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string path;

    private AppSettingsStore(
        string path,
        UiLanguage uiLanguage,
        TranscriptionLanguage transcriptionLanguage)
    {
        this.path = path;
        UiLanguage = uiLanguage;
        TranscriptionLanguage = transcriptionLanguage;
    }

    internal static AppSettingsStore Current { get; private set; } =
        CreateDefaults();

    internal UiLanguage UiLanguage { get; private set; }

    internal TranscriptionLanguage TranscriptionLanguage { get; private set; }

    internal static void LoadCurrent()
    {
        AppSettingsStore defaults = CreateDefaults();
        try
        {
            if (!File.Exists(defaults.path))
            {
                Current = defaults;
                return;
            }

            string json = File.ReadAllText(defaults.path);
            SettingsDocument? document = JsonSerializer.Deserialize<SettingsDocument>(
                json,
                JsonOptions);
            UiLanguage ui = LocalizationManager.FromCode(
                document?.UiLanguage,
                defaults.UiLanguage);
            TranscriptionLanguage transcription = ParseTranscriptionLanguage(
                document?.TranscriptionLanguage,
                LanguageFor(ui));
            Current = new AppSettingsStore(defaults.path, ui, transcription);
        }
        catch
        {
            Current = defaults;
        }
    }

    internal void SetUiLanguage(UiLanguage language)
    {
        UiLanguage = language;
        SaveSafely();
    }

    internal void SetTranscriptionLanguage(TranscriptionLanguage language)
    {
        if (language is not (TranscriptionLanguage.Russian
            or TranscriptionLanguage.English
            or TranscriptionLanguage.Italian))
        {
            return;
        }

        TranscriptionLanguage = language;
        SaveSafely();
    }

    private static AppSettingsStore CreateDefaults()
    {
        UiLanguage ui = LocalizationManager.SystemDefault;
        string local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new AppSettingsStore(
            Path.Combine(local, "Megatranscriber", "settings.json"),
            ui,
            LanguageFor(ui));
    }

    private static TranscriptionLanguage LanguageFor(UiLanguage language) =>
        language switch
        {
            UiLanguage.Russian => TranscriptionLanguage.Russian,
            UiLanguage.Italian => TranscriptionLanguage.Italian,
            _ => TranscriptionLanguage.English,
        };

    private static TranscriptionLanguage ParseTranscriptionLanguage(
        string? code,
        TranscriptionLanguage fallback) => code?.ToLowerInvariant() switch
        {
            "ru" => TranscriptionLanguage.Russian,
            "en" => TranscriptionLanguage.English,
            "it" => TranscriptionLanguage.Italian,
            _ => fallback,
        };

    private void SaveSafely()
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            string json = JsonSerializer.Serialize(
                new SettingsDocument(
                    LocalizationManager.ToCode(UiLanguage),
                    TranscriptionLanguage switch
                    {
                        TranscriptionLanguage.Russian => "ru",
                        TranscriptionLanguage.Italian => "it",
                        _ => "en",
                    }),
                JsonOptions);
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // Settings are optional and must never make the app unusable.
        }
    }

    private sealed record SettingsDocument(
        string UiLanguage,
        string TranscriptionLanguage);
}
