using PolinMegatranscriber.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace PolinMegatranscriber.App;

internal enum UiRunState
{
    Idle,
    Preparing,
    DownloadingModel,
    Processing,
    Cancelling,
    Cancelled,
    Success,
    Failure,
}

internal sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IModelManager modelManager;
    private readonly IMediaInspector mediaInspector;
    private readonly IProcessingService processingService;
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? inspectionCancellation;
    private string? inputPath;
    private string outputDirectory = "Выберите файл";
    private ProcessingMode? selectedMode;
    private WhisperModel selectedModel = WhisperModel.Small;
    private UiRunState state;
    private bool isInspecting;
    private double progressFraction;
    private string stageText = "";
    private string statusMessage = "";
    private string smallStatus = "Проверяем…";
    private string mediumStatus = "Проверяем…";
    private ProcessingResult? result;
    private bool textCopied;

    internal MainViewModel(
        IModelManager modelManager,
        IMediaInspector mediaInspector,
        IProcessingService processingService)
    {
        this.modelManager = modelManager;
        this.mediaInspector = mediaInspector;
        this.processingService = processingService;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ObservableCollection<string> OutputFileNames { get; } = [];

    internal IReadOnlyList<string> OutputFiles => result?.OutputFiles ?? [];

    internal string? TextOutputPath => result?.OutputFiles.FirstOrDefault(
        path => string.Equals(
            Path.GetExtension(path),
            ".txt",
            StringComparison.OrdinalIgnoreCase));

    public string FileName => inputPath is null
        ? "Перетащите запись сюда"
        : Path.GetFileName(inputPath);

    public string FileDetails
    {
        get
        {
            if (isInspecting)
            {
                return "Проверяем файл…";
            }

            if (inputPath is null)
            {
                return "WEBM, MP4, MOV, MP3, M4A, WAV и другие форматы FFmpeg";
            }

            try
            {
                return FormatBytes(new FileInfo(inputPath).Length);
            }
            catch
            {
                return "Файл выбран";
            }
        }
    }

    public string FileBadge => inputPath is null
        ? "ЛОКАЛЬНО"
        : Path.GetExtension(inputPath).TrimStart('.').ToUpperInvariant();

    public string FileButtonText => inputPath is null
        ? "Выбрать файл…"
        : "Выбрать другой файл…";

    public string OutputDirectory
    {
        get => outputDirectory;
        private set => Set(ref outputDirectory, value);
    }

    public string SelectedModeTitle => selectedMode switch
    {
        ProcessingMode.AudioOnly => "Только аудио · MP3",
        ProcessingMode.TextOnly => "Только текст · TXT + SRT",
        ProcessingMode.AudioAndText => "Аудио и текст · MP3 + TXT + SRT",
        _ => "Режим не выбран",
    };

    public bool IsAudioOnly
    {
        get => selectedMode == ProcessingMode.AudioOnly;
        set { if (value) SelectMode(ProcessingMode.AudioOnly); }
    }

    public bool IsTextOnly
    {
        get => selectedMode == ProcessingMode.TextOnly;
        set { if (value) SelectMode(ProcessingMode.TextOnly); }
    }

    public bool IsAudioAndText
    {
        get => selectedMode == ProcessingMode.AudioAndText;
        set { if (value) SelectMode(ProcessingMode.AudioAndText); }
    }

    public bool IsSmall
    {
        get => selectedModel == WhisperModel.Small;
        set { if (value) SelectModel(WhisperModel.Small); }
    }

    public bool IsMedium
    {
        get => selectedModel == WhisperModel.Medium;
        set { if (value) SelectModel(WhisperModel.Medium); }
    }

    public bool ShowsModelSelection => selectedMode is
        ProcessingMode.TextOnly or ProcessingMode.AudioAndText;

    public string SmallStatus => smallStatus;

    public string MediumStatus => mediumStatus;

    public bool IsBusy => state is UiRunState.Preparing
        or UiRunState.DownloadingModel
        or UiRunState.Processing
        or UiRunState.Cancelling;

    public bool InputsEnabled => !IsBusy;

    public bool IsCompact => IsBusy || state == UiRunState.Success;

    public bool ShowProgress => IsBusy;

    public bool ShowCancelled => state == UiRunState.Cancelled;

    public bool ShowFailure => state == UiRunState.Failure;

    public bool ShowSuccess => state == UiRunState.Success;

    public bool ShowStartButton => !IsBusy && state != UiRunState.Success;

    public bool ShowCancelButton => IsBusy;

    public bool CanStart => inputPath is not null
        && selectedMode is not null
        && Directory.Exists(outputDirectory)
        && !IsBusy
        && !isInspecting;

    public bool IsInspecting => isInspecting;

    public double ProgressPercent => progressFraction * 100.0;

    public string ProgressText => $"{Math.Round(ProgressPercent):0}%";

    public string StageText => stageText;

    public string CancelButtonText => state == UiRunState.Cancelling
        ? "Отменяем…"
        : "Отменить";

    public string StatusMessage => statusMessage;

    public bool HasTextOutput => TextOutputPath is not null;

    public string CopyTextLabel => textCopied ? "Текст скопирован" : "Скопировать текст";

    internal async Task InitializeAsync()
    {
        await Task.WhenAll(
            RefreshModelStatusAsync(WhisperModel.Small),
            RefreshModelStatusAsync(WhisperModel.Medium));
    }

    internal async Task SelectInputAsync(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        inspectionCancellation?.Cancel();
        inspectionCancellation?.Dispose();
        inspectionCancellation = new CancellationTokenSource();
        CancellationToken token = inspectionCancellation.Token;
        inputPath = Path.GetFullPath(path);
        result = null;
        OutputFileNames.Clear();
        state = UiRunState.Idle;
        statusMessage = "";
        isInspecting = true;
        RaiseState();
        RaiseFile();

        try
        {
            RequireReadableOrdinaryFile(inputPath);
            _ = await mediaInspector.InspectAsync(inputPath, token);
            OutputDirectory = Path.GetDirectoryName(inputPath)
                ?? throw new IOException();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            inputPath = null;
            state = UiRunState.Failure;
            statusMessage = SafeMessage(
                exception,
                "Не удалось проверить выбранный медиафайл.");
        }
        finally
        {
            isInspecting = false;
            RaiseState();
            RaiseFile();
        }
    }

    internal void SetOutputDirectory(string path)
    {
        if (IsBusy || !Directory.Exists(path))
        {
            return;
        }

        OutputDirectory = Path.GetFullPath(path);
        OnPropertyChanged(nameof(CanStart));
    }

    internal async Task StartAsync()
    {
        if (!CanStart || inputPath is null || selectedMode is null)
        {
            return;
        }

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        CancellationToken token = operationCancellation.Token;
        ProcessingMode mode = selectedMode.Value;
        string input = inputPath;
        string destination = outputDirectory;
        string? modelPath = null;
        double processingBase = 0.0;
        result = null;
        OutputFileNames.Clear();
        progressFraction = 0.0;
        stageText = "Проверяем файл";
        statusMessage = "";
        state = UiRunState.Preparing;
        RaiseState();

        try
        {
            if (mode is ProcessingMode.TextOnly or ProcessingMode.AudioAndText)
            {
                stageText = "Проверяем модель";
                RaiseProgress();
                ModelInstallationStatus modelStatus =
                    await modelManager.GetStatusAsync(selectedModel, token);
                if (modelStatus != ModelInstallationStatus.Verified)
                {
                    state = UiRunState.DownloadingModel;
                    stageText = "Загружаем модель — это потребуется только один раз";
                    processingBase = 0.15;
                    RaiseState();
                    var modelProgress = new Progress<ModelDownloadProgress>(value =>
                    {
                        progressFraction = Math.Max(
                            progressFraction,
                            Math.Clamp(value.Fraction, 0.0, 1.0) * 0.15);
                        RaiseProgress();
                    });
                    modelPath = await modelManager.DownloadAndInstallAsync(
                        selectedModel,
                        modelProgress,
                        token);
                    SetModelStatus(selectedModel, "Установлена");
                }
                else
                {
                    modelPath = await modelManager.GetVerifiedPathAsync(
                        selectedModel,
                        token);
                }

                if (modelPath is null)
                {
                    throw new InvalidOperationException(
                        "Модель распознавания недоступна или повреждена.");
                }
            }

            state = UiRunState.Processing;
            RaiseState();
            double progressBase = processingBase;
            var processingProgress = new Progress<ProcessingProgress>(value =>
            {
                progressFraction = Math.Max(
                    progressFraction,
                    progressBase + (1.0 - progressBase)
                    * Math.Clamp(value.Fraction, 0.0, 1.0));
                stageText = StageFor(value.Phase);
                RaiseProgress();
            });
            result = await processingService.ProcessAsync(
                new ProcessingRequest(
                    input,
                    mode,
                    destination,
                    modelPath,
                    TranscriptionLanguage.Russian),
                processingProgress,
                token);

            progressFraction = 1.0;
            stageText = "Сохраняем результаты";
            foreach (string output in result.OutputFiles)
            {
                OutputFileNames.Add(Path.GetFileName(output));
            }
            state = UiRunState.Success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            state = UiRunState.Cancelled;
            statusMessage = "Можно изменить параметры или запустить обработку снова.";
        }
        catch (Exception exception)
        {
            state = UiRunState.Failure;
            statusMessage = SafeMessage(
                exception,
                "Не удалось выполнить локальную обработку.");
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            RaiseState();
            RaiseProgress();
        }
    }

    internal void Cancel()
    {
        if (!IsBusy || operationCancellation is null)
        {
            return;
        }

        state = UiRunState.Cancelling;
        stageText = "Отменяем…";
        RaiseState();
        operationCancellation.Cancel();
    }

    internal void ResetForAnotherFile()
    {
        if (IsBusy)
        {
            return;
        }

        result = null;
        OutputFileNames.Clear();
        state = UiRunState.Idle;
        progressFraction = 0;
        statusMessage = "";
        RaiseState();
    }

    internal async Task MarkTextCopiedAsync()
    {
        textCopied = true;
        OnPropertyChanged(nameof(CopyTextLabel));
        await Task.Delay(TimeSpan.FromSeconds(2));
        textCopied = false;
        OnPropertyChanged(nameof(CopyTextLabel));
    }

    private void SelectMode(ProcessingMode mode)
    {
        if (IsBusy)
        {
            return;
        }

        selectedMode = mode;
        OnPropertyChanged(nameof(IsAudioOnly));
        OnPropertyChanged(nameof(IsTextOnly));
        OnPropertyChanged(nameof(IsAudioAndText));
        OnPropertyChanged(nameof(ShowsModelSelection));
        OnPropertyChanged(nameof(SelectedModeTitle));
        OnPropertyChanged(nameof(CanStart));
    }

    private void SelectModel(WhisperModel model)
    {
        if (IsBusy)
        {
            return;
        }

        selectedModel = model;
        OnPropertyChanged(nameof(IsSmall));
        OnPropertyChanged(nameof(IsMedium));
    }

    private async Task RefreshModelStatusAsync(WhisperModel model)
    {
        string text;
        try
        {
            ModelInstallationStatus status = await modelManager.GetStatusAsync(model);
            text = status switch
            {
                ModelInstallationStatus.Verified => "Установлена",
                ModelInstallationStatus.Corrupted => "Повреждена · загрузим заново",
                _ => "Требуется загрузка",
            };
        }
        catch
        {
            text = "Статус недоступен";
        }

        SetModelStatus(model, text);
    }

    private void SetModelStatus(WhisperModel model, string text)
    {
        if (model == WhisperModel.Small)
        {
            smallStatus = text;
            OnPropertyChanged(nameof(SmallStatus));
        }
        else
        {
            mediumStatus = text;
            OnPropertyChanged(nameof(MediumStatus));
        }
    }

    private static void RequireReadableOrdinaryFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new IOException();
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
    }

    private static string StageFor(ProcessingPhase phase) => phase switch
    {
        ProcessingPhase.Preflight => "Проверяем файл",
        ProcessingPhase.MediaProcessing => "Извлекаем аудио",
        ProcessingPhase.Transcription => "Распознаём речь",
        _ => "Сохраняем результаты",
    };

    private static string SafeMessage(Exception exception, string fallback)
    {
        string message = exception.Message.Trim();
        bool looksSafe = message.Length is > 0 and <= 300
            && !message.Contains('\\')
            && !message.Contains('/')
            && !message.Contains('\r')
            && !message.Contains('\n')
            && !message.Contains("stderr", StringComparison.OrdinalIgnoreCase);
        return looksSafe ? message : fallback;
    }

    private static string FormatBytes(long bytes)
    {
        const double kibibyte = 1024.0;
        const double mebibyte = kibibyte * 1024.0;
        const double gibibyte = mebibyte * 1024.0;

        if (bytes >= gibibyte)
        {
            return $"{bytes / gibibyte:0.0} ГБ";
        }

        if (bytes >= mebibyte)
        {
            return $"{bytes / mebibyte:0.0} МБ";
        }

        if (bytes >= kibibyte)
        {
            return $"{bytes / kibibyte:0.#} КБ";
        }

        return $"{bytes} Б";
    }

    private void RaiseFile()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileDetails));
        OnPropertyChanged(nameof(FileBadge));
        OnPropertyChanged(nameof(FileButtonText));
        OnPropertyChanged(nameof(IsInspecting));
    }

    private void RaiseProgress()
    {
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(StageText));
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(InputsEnabled));
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ShowCancelled));
        OnPropertyChanged(nameof(ShowFailure));
        OnPropertyChanged(nameof(ShowSuccess));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasTextOutput));
        OnPropertyChanged(nameof(CancelButtonText));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
