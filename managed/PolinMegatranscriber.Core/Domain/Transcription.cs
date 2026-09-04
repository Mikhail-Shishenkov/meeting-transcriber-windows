using System.Collections.ObjectModel;

namespace PolinMegatranscriber.Core;

public enum TranscriptionLanguage
{
    Automatic = 0,
    Russian = 1,
}

public sealed record TranscriptionRequest(
    string ModelPath,
    string WavPath,
    TranscriptionLanguage Language = TranscriptionLanguage.Automatic);

public sealed record TranscriptionSegment(
    long StartMilliseconds,
    long EndMilliseconds,
    string Text);

public sealed class TranscriptionResult
{
    internal TranscriptionResult(
        TranscriptionSegment[] segments,
        string? detectedLanguage)
    {
        Segments = new ReadOnlyCollection<TranscriptionSegment>(segments);
        DetectedLanguage = detectedLanguage;
    }

    public IReadOnlyList<TranscriptionSegment> Segments { get; }

    public string? DetectedLanguage { get; }
}

public enum TranscriptionError
{
    RuntimeUnavailable,
    InferenceInProgress,
    InvalidModel,
    InvalidWav,
    UnsupportedWav,
    InferenceFailed,
    InvalidResult,
}

public sealed class TranscriptionException : Exception
{
    internal TranscriptionException(
        TranscriptionError error,
        Exception? innerException = null)
        : base(MessageFor(error), innerException)
    {
        Error = error;
    }

    public TranscriptionError Error { get; }

    private static string MessageFor(TranscriptionError error) => error switch
    {
        TranscriptionError.RuntimeUnavailable =>
            "Локальный движок распознавания недоступен.",
        TranscriptionError.InferenceInProgress =>
            "Распознавание уже выполняется.",
        TranscriptionError.InvalidModel =>
            "Проверенная модель недоступна для чтения.",
        TranscriptionError.InvalidWav =>
            "Временный WAV-файл повреждён или недоступен.",
        TranscriptionError.UnsupportedWav =>
            "Требуется mono 16 kHz PCM16 WAV.",
        TranscriptionError.InferenceFailed =>
            "Локальное распознавание завершилось с ошибкой.",
        TranscriptionError.InvalidResult =>
            "Движок вернул некорректные данные транскрипции.",
        _ => "Локальное распознавание завершилось с ошибкой.",
    };
}
