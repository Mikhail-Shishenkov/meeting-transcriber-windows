using System.Windows;

namespace PolinMegatranscriber.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppSettingsStore.LoadCurrent();
        LocalizationManager.Apply(AppSettingsStore.Current.UiLanguage);
        base.OnStartup(e);
    }
}
