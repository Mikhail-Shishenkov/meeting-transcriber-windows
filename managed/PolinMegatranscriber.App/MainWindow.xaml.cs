using Microsoft.Win32;
using PolinMegatranscriber.Core;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PolinMegatranscriber.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        var locator = new WindowsMediaToolLocator();
        viewModel = new MainViewModel(
            new ModelManager(),
            new FFprobeMediaInspector(locator),
            new ProcessingService(locator));
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) =>
        await viewModel.InitializeAsync();

    private async void ChooseFile_Click(object sender, RoutedEventArgs e) =>
        await ChooseFileAsync();

    private async Task ChooseFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите запись",
            Filter = "Медиафайлы|*.webm;*.mp4;*.mov;*.mp3;*.m4a;*.wav|Все файлы|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await viewModel.SelectInputAsync(dialog.FileName);
        }
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Куда сохранить результаты?",
            InitialDirectory = Directory.Exists(viewModel.OutputDirectory)
                ? viewModel.OutputDirectory
                : null,
        };
        if (dialog.ShowDialog(this) == true)
        {
            viewModel.SetOutputDirectory(dialog.FolderName);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) =>
        await viewModel.StartAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        viewModel.Cancel();

    private async void AnotherFile_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ResetForAnotherFile();
        await ChooseFileAsync();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            await viewModel.SelectInputAsync(paths[0]);
        }
    }

    private void Appearance_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem item }
            || item.Tag is not string tag
            || !Enum.TryParse(tag, out AppAppearance appearance))
        {
            return;
        }

        ThemeManager.Apply(appearance);
    }

    private void RevealResult_Click(object sender, RoutedEventArgs e)
    {
        string? path = viewModel.OutputFiles.FirstOrDefault(IsReadableFile);
        if (path is null)
        {
            return;
        }

        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(path);
        _ = Process.Start(startInfo);
    }

    private void OpenResultsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(viewModel.OutputDirectory))
        {
            return;
        }

        _ = Process.Start(new ProcessStartInfo(viewModel.OutputDirectory)
        {
            UseShellExecute = true,
        });
    }

    private void OpenText_Click(object sender, RoutedEventArgs e)
    {
        string? path = viewModel.TextOutputPath;
        if (!IsReadableFile(path))
        {
            return;
        }

        _ = Process.Start(new ProcessStartInfo(path!)
        {
            UseShellExecute = true,
        });
    }

    private async void CopyText_Click(object sender, RoutedEventArgs e)
    {
        string? path = viewModel.TextOutputPath;
        if (!IsReadableFile(path))
        {
            return;
        }

        string text = await File.ReadAllTextAsync(path!);
        Clipboard.SetText(text);
        await viewModel.MarkTextCopiedAsync();
    }

    private static bool IsReadableFile(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path)
                && File.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch
        {
            return false;
        }
    }
}
