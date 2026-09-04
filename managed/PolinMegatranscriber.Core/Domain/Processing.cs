using System.Collections.ObjectModel;

namespace PolinMegatranscriber.Core;

public enum ProcessingMode
{
    AudioOnly,
    TextOnly,
    AudioAndText,
}

public enum ProcessingPhase
{
    Preflight,
    MediaProcessing,
    Transcription,
    Exporting,
    Publishing,
    Completed,
}

public readonly record struct ProcessingProgress(
    ProcessingPhase Phase,
    double Fraction);

public sealed record ProcessingRequest(
    string InputMediaPath,
    ProcessingMode Mode,
    string ResultsDirectory,
    string? ModelPath = null,
    TranscriptionLanguage Language = TranscriptionLanguage.Russian);

public sealed class ProcessingResult
{
    internal ProcessingResult(
        Guid jobId,
        string[] outputFiles,
        TranscriptionResult? transcription)
    {
        JobId = jobId;
        OutputFiles = new ReadOnlyCollection<string>(outputFiles);
        Transcription = transcription;
    }

    public Guid JobId { get; }

    public IReadOnlyList<string> OutputFiles { get; }

    public TranscriptionResult? Transcription { get; }
}

public enum ProcessingError
{
    ProcessingInProgress,
    InvalidInput,
    PreflightFailed,
    NoAudioStream,
    OutputConflict,
    MediaProcessingFailed,
    ModelUnavailableOrInvalid,
    TranscriptionFailed,
    ExportFailed,
    CleanupFailed,
}

public sealed class ProcessingException : Exception
{
    internal ProcessingException(ProcessingError error)
        : base(MessageFor(error))
    {
        Error = error;
    }

    public ProcessingError Error { get; }

    private static string MessageFor(ProcessingError error) => error switch
    {
        ProcessingError.ProcessingInProgress =>
            "Другое задание уже выполняется.",
        ProcessingError.InvalidInput =>
            "Входной файл или параметры задания недоступны.",
        ProcessingError.PreflightFailed =>
            "Не удалось проверить входной медиафайл и папку результатов.",
        ProcessingError.NoAudioStream =>
            "Во входном файле не найден аудиопоток.",
        ProcessingError.OutputConflict =>
            "Один из итоговых файлов уже существует.",
        ProcessingError.MediaProcessingFailed =>
            "Не удалось подготовить аудио для задания.",
        ProcessingError.ModelUnavailableOrInvalid =>
            "Модель распознавания недоступна или повреждена.",
        ProcessingError.TranscriptionFailed =>
            "Не удалось распознать речь.",
        ProcessingError.ExportFailed =>
            "Не удалось подготовить или опубликовать результаты.",
        ProcessingError.CleanupFailed =>
            "Не удалось безопасно очистить временные файлы задания.",
        _ => "Не удалось выполнить задание.",
    };
}
