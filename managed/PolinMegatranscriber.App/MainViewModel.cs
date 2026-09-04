using PolinMegatranscriber.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace PolinMegatranscriber.App;

internal enum UiRunState { Idle, Preparing, DownloadingModel, Processing, Cancelling, Cancelled, Success, Failure }

internal sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IModelManager modelManager;
    private readonly IMediaInspector mediaInspector;
    private readonly IProcessingService processingService;
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? inspectionCancellation;
    private string? inputPath;
    private string outputDirectory = "";
    private ProcessingMode? selectedMode;
    private WhisperModel selectedModel = WhisperModel.Small;
    private TranscriptionLanguage transcriptionLanguage;
    private UiRunState state;
    private bool isInspecting;
    private double progressFraction;
    private string stageKey = "StageCheckingFile";
    private string statusKey = "";
    private string smallStatusKey = "ModelChecking";
    private string mediumStatusKey = "ModelChecking";
    private ProcessingResult? result;
    private bool textCopied;

    internal MainViewModel(IModelManager modelManager, IMediaInspector mediaInspector, IProcessingService processingService)
    {
        this.modelManager = modelManager;
        this.mediaInspector = mediaInspector;
        this.processingService = processingService;
        transcriptionLanguage = AppSettingsStore.Current.TranscriptionLanguage;
        LocalizationManager.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal ObservableCollection<string> OutputFileNames { get; } = [];
    internal IReadOnlyList<string> OutputFiles => result?.OutputFiles ?? [];
    internal string? TextOutputPath => result?.OutputFiles.FirstOrDefault(path =>
        string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase));

    public bool HasInputFile => inputPath is not null;
    public string FileName => inputPath is null ? L("DropTitle") : Path.GetFileName(inputPath);
    public string FileDetails
    {
        get
        {
            if (isInspecting) return L("StageCheckingFileEllipsis");
            if (inputPath is null) return L("FileFormats");
            try { return FormatBytes(new FileInfo(inputPath).Length); }
            catch { return L("FileSelected"); }
        }
    }
    public string FileBadge => inputPath is null ? "" : Path.GetExtension(inputPath).TrimStart('.').ToUpperInvariant();
    public string FileButtonText => L(inputPath is null ? "ChooseFile" : "ChooseAnotherFile");
    public string OutputDirectory { get => outputDirectory; private set => Set(ref outputDirectory, value); }
    public string SelectedModeTitle => selectedMode switch
    {
        ProcessingMode.AudioOnly => $"{L("ModeAudioOnly")} · MP3",
        ProcessingMode.TextOnly => $"{L("ModeTextOnly")} · TXT + SRT",
        ProcessingMode.AudioAndText => $"{L("ModeAudioText")} · MP3 + TXT + SRT",
        _ => L("ModeNotSelected"),
    };

    public bool IsAudioOnly { get => selectedMode == ProcessingMode.AudioOnly; set { if (value) SelectMode(ProcessingMode.AudioOnly); } }
    public bool IsTextOnly { get => selectedMode == ProcessingMode.TextOnly; set { if (value) SelectMode(ProcessingMode.TextOnly); } }
    public bool IsAudioAndText { get => selectedMode == ProcessingMode.AudioAndText; set { if (value) SelectMode(ProcessingMode.AudioAndText); } }
    public bool IsSmall { get => selectedModel == WhisperModel.Small; set { if (value) SelectModel(WhisperModel.Small); } }
    public bool IsMedium { get => selectedModel == WhisperModel.Medium; set { if (value) SelectModel(WhisperModel.Medium); } }
    public bool IsRecordingRussian { get => transcriptionLanguage == TranscriptionLanguage.Russian; set { if (value) SelectTranscriptionLanguage(TranscriptionLanguage.Russian); } }
    public bool IsRecordingEnglish { get => transcriptionLanguage == TranscriptionLanguage.English; set { if (value) SelectTranscriptionLanguage(TranscriptionLanguage.English); } }
    public bool IsRecordingItalian { get => transcriptionLanguage == TranscriptionLanguage.Italian; set { if (value) SelectTranscriptionLanguage(TranscriptionLanguage.Italian); } }
    public bool ShowsModelSelection => selectedMode is ProcessingMode.TextOnly or ProcessingMode.AudioAndText;
    public string SmallStatus => L(smallStatusKey);
    public string MediumStatus => L(mediumStatusKey);
    public bool IsBusy => state is UiRunState.Preparing or UiRunState.DownloadingModel or UiRunState.Processing or UiRunState.Cancelling;
    public bool InputsEnabled => !IsBusy;
    public bool IsCompact => IsBusy || state == UiRunState.Success;
    public bool ShowProgress => IsBusy;
    public bool ShowCancelled => state == UiRunState.Cancelled;
    public bool ShowFailure => state == UiRunState.Failure;
    public bool ShowSuccess => state == UiRunState.Success;
    public bool ShowStartButton => !IsBusy && state != UiRunState.Success;
    public bool ShowCancelButton => IsBusy;
    public bool CanStart => inputPath is not null && selectedMode is not null && Directory.Exists(outputDirectory) && !IsBusy && !isInspecting;
    public bool IsInspecting => isInspecting;
    public double ProgressPercent => progressFraction * 100.0;
    public string ProgressText => $"{Math.Round(ProgressPercent):0}%";
    public string StageText => L(stageKey);
    public string CancelButtonText => L(state == UiRunState.Cancelling ? "StageCancelling" : "Cancel");
    public string StatusMessage => string.IsNullOrEmpty(statusKey) ? "" : L(statusKey);
    public bool HasTextOutput => TextOutputPath is not null;
    public string CopyTextLabel => L(textCopied ? "TextCopied" : "CopyText");

    internal async Task InitializeAsync() => await Task.WhenAll(
        RefreshModelStatusAsync(WhisperModel.Small), RefreshModelStatusAsync(WhisperModel.Medium));

    internal async Task SelectInputAsync(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path)) return;
        inspectionCancellation?.Cancel();
        inspectionCancellation?.Dispose();
        inspectionCancellation = new CancellationTokenSource();
        CancellationToken token = inspectionCancellation.Token;
        inputPath = Path.GetFullPath(path);
        result = null;
        OutputFileNames.Clear();
        state = UiRunState.Idle;
        statusKey = "";
        isInspecting = true;
        RaiseState(); RaiseFile();
        try
        {
            RequireReadableOrdinaryFile(inputPath);
            _ = await mediaInspector.InspectAsync(inputPath, token);
            OutputDirectory = Path.GetDirectoryName(inputPath) ?? throw new IOException();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            inputPath = null;
            state = UiRunState.Failure;
            statusKey = ErrorKey(exception, "ErrorInspect");
        }
        finally
        {
            isInspecting = false;
            RaiseState(); RaiseFile();
        }
    }

    internal void SetOutputDirectory(string path)
    {
        if (IsBusy || !Directory.Exists(path)) return;
        OutputDirectory = Path.GetFullPath(path);
        OnPropertyChanged(nameof(CanStart));
    }

    internal async Task StartAsync()
    {
        if (!CanStart || inputPath is null || selectedMode is null) return;
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        CancellationToken token = operationCancellation.Token;
        ProcessingMode mode = selectedMode.Value;
        TranscriptionLanguage operationLanguage = transcriptionLanguage;
        string input = inputPath;
        string destination = outputDirectory;
        string? modelPath = null;
        double processingBase = 0.0;
        result = null;
        OutputFileNames.Clear();
        progressFraction = 0.0;
        stageKey = "StageCheckingFile";
        statusKey = "";
        state = UiRunState.Preparing;
        RaiseState();
        try
        {
            if (mode is ProcessingMode.TextOnly or ProcessingMode.AudioAndText)
            {
                stageKey = "StageCheckingModel"; RaiseProgress();
                ModelInstallationStatus modelStatus = await modelManager.GetStatusAsync(selectedModel, token);
                if (modelStatus != ModelInstallationStatus.Verified)
                {
                    state = UiRunState.DownloadingModel;
                    stageKey = "StageDownloadingModel";
                    processingBase = 0.15;
                    RaiseState();
                    var modelProgress = new Progress<ModelDownloadProgress>(value =>
                    {
                        progressFraction = Math.Max(progressFraction, Math.Clamp(value.Fraction, 0.0, 1.0) * 0.15);
                        RaiseProgress();
                    });
                    modelPath = await modelManager.DownloadAndInstallAsync(selectedModel, modelProgress, token);
                    SetModelStatus(selectedModel, "ModelInstalled");
                }
                else
                {
                    modelPath = await modelManager.GetVerifiedPathAsync(selectedModel, token);
                }
                if (modelPath is null) throw new InvalidOperationException();
            }

            state = UiRunState.Processing; RaiseState();
            double progressBase = processingBase;
            var processingProgress = new Progress<ProcessingProgress>(value =>
            {
                progressFraction = Math.Max(progressFraction,
                    progressBase + (1.0 - progressBase) * Math.Clamp(value.Fraction, 0.0, 1.0));
                stageKey = StageKeyFor(value.Phase);
                RaiseProgress();
            });
            result = await processingService.ProcessAsync(
                new ProcessingRequest(input, mode, destination, modelPath, operationLanguage),
                processingProgress, token);
            progressFraction = 1.0;
            stageKey = "StageSaving";
            foreach (string output in result.OutputFiles) OutputFileNames.Add(Path.GetFileName(output));
            state = UiRunState.Success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            state = UiRunState.Cancelled; statusKey = "CancelledMessage";
        }
        catch (Exception exception)
        {
            state = UiRunState.Failure; statusKey = ErrorKey(exception, "ErrorProcessing");
        }
        finally
        {
            operationCancellation?.Dispose(); operationCancellation = null;
            RaiseState(); RaiseProgress();
        }
    }

    internal void Cancel()
    {
        if (!IsBusy || operationCancellation is null) return;
        state = UiRunState.Cancelling; stageKey = "StageCancelling";
        RaiseState(); operationCancellation.Cancel();
    }

    internal void ResetForAnotherFile()
    {
        if (IsBusy) return;
        result = null; OutputFileNames.Clear(); state = UiRunState.Idle;
        progressFraction = 0; statusKey = ""; RaiseState();
    }

    internal async Task MarkTextCopiedAsync()
    {
        textCopied = true; OnPropertyChanged(nameof(CopyTextLabel));
        await Task.Delay(TimeSpan.FromSeconds(2));
        textCopied = false; OnPropertyChanged(nameof(CopyTextLabel));
    }

    private void SelectMode(ProcessingMode mode)
    {
        if (IsBusy) return;
        selectedMode = mode;
        OnPropertyChanged(nameof(IsAudioOnly)); OnPropertyChanged(nameof(IsTextOnly));
        OnPropertyChanged(nameof(IsAudioAndText)); OnPropertyChanged(nameof(ShowsModelSelection));
        OnPropertyChanged(nameof(SelectedModeTitle)); OnPropertyChanged(nameof(CanStart));
    }

    private void SelectModel(WhisperModel model)
    {
        if (IsBusy) return;
        selectedModel = model; OnPropertyChanged(nameof(IsSmall)); OnPropertyChanged(nameof(IsMedium));
    }

    private void SelectTranscriptionLanguage(TranscriptionLanguage language)
    {
        if (IsBusy) return;
        transcriptionLanguage = language;
        AppSettingsStore.Current.SetTranscriptionLanguage(language);
        OnPropertyChanged(nameof(IsRecordingRussian)); OnPropertyChanged(nameof(IsRecordingEnglish));
        OnPropertyChanged(nameof(IsRecordingItalian));
    }

    private async Task RefreshModelStatusAsync(WhisperModel model)
    {
        string key;
        try
        {
            key = await modelManager.GetStatusAsync(model) switch
            {
                ModelInstallationStatus.Verified => "ModelInstalled",
                ModelInstallationStatus.Corrupted => "ModelCorrupted",
                _ => "ModelDownloadRequired",
            };
        }
        catch { key = "ModelStatusUnavailable"; }
        SetModelStatus(model, key);
    }

    private void SetModelStatus(WhisperModel model, string key)
    {
        if (model == WhisperModel.Small) { smallStatusKey = key; OnPropertyChanged(nameof(SmallStatus)); }
        else { mediumStatusKey = key; OnPropertyChanged(nameof(MediumStatus)); }
    }

    private static void RequireReadableOrdinaryFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0) throw new IOException();
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static string StageKeyFor(ProcessingPhase phase) => phase switch
    {
        ProcessingPhase.Preflight => "StageCheckingFile",
        ProcessingPhase.MediaProcessing => "StageExtractingAudio",
        ProcessingPhase.Transcription => "StageTranscribing",
        _ => "StageSaving",
    };

    private static string ErrorKey(Exception exception, string fallback) => exception switch
    {
        MediaInspectionException { Error: MediaInspectionError.ToolUnavailable } => "ErrorMediaTools",
        MediaInspectionException { Error: MediaInspectionError.NoAudioStream } => "ErrorNoAudio",
        MediaInspectionException { Error: MediaInspectionError.InvalidOrUnsupportedMedia } => "ErrorInvalidMedia",
        ProcessingException { Error: ProcessingError.NoAudioStream } => "ErrorNoAudio",
        ProcessingException { Error: ProcessingError.OutputConflict } => "ErrorOutputConflict",
        ProcessingException { Error: ProcessingError.ModelUnavailableOrInvalid } => "ErrorModel",
        ModelManagerException { Error: ModelManagementError.NetworkFailure or ModelManagementError.HttpFailure or ModelManagementError.DownloadFailed } => "ErrorNetwork",
        ModelManagerException or InvalidOperationException => "ErrorModel",
        _ => fallback,
    };

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024.0, mb = kb * 1024.0, gb = mb * 1024.0;
        if (bytes >= gb) return $"{bytes / gb:0.0} {L("UnitGB")}";
        if (bytes >= mb) return $"{bytes / mb:0.0} {L("UnitMB")}";
        if (bytes >= kb) return $"{bytes / kb:0.#} {L("UnitKB")}";
        return $"{bytes} {L("UnitB")}";
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RaiseFile(); OnPropertyChanged(nameof(SelectedModeTitle));
        OnPropertyChanged(nameof(SmallStatus)); OnPropertyChanged(nameof(MediumStatus));
        OnPropertyChanged(nameof(StageText)); OnPropertyChanged(nameof(CancelButtonText));
        OnPropertyChanged(nameof(StatusMessage)); OnPropertyChanged(nameof(CopyTextLabel));
    }

    private void RaiseFile()
    {
        OnPropertyChanged(nameof(HasInputFile)); OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileDetails)); OnPropertyChanged(nameof(FileBadge));
        OnPropertyChanged(nameof(FileButtonText)); OnPropertyChanged(nameof(IsInspecting));
    }
    private void RaiseProgress()
    {
        OnPropertyChanged(nameof(ProgressPercent)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(StageText));
    }
    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(InputsEnabled)); OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(ShowProgress)); OnPropertyChanged(nameof(ShowCancelled)); OnPropertyChanged(nameof(ShowFailure));
        OnPropertyChanged(nameof(ShowSuccess)); OnPropertyChanged(nameof(ShowStartButton)); OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(StatusMessage)); OnPropertyChanged(nameof(HasTextOutput));
        OnPropertyChanged(nameof(CancelButtonText));
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; OnPropertyChanged(name);
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string L(string key) => LocalizationManager.Get(key);
}
