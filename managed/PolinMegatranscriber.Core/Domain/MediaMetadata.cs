using System.Collections.ObjectModel;

namespace PolinMegatranscriber.Core;

public sealed record AudioStreamMetadata(
    int Index,
    string CodecName,
    int SampleRateHz,
    int ChannelCount,
    TimeSpan? Duration);

public sealed class MediaMetadata
{
    internal MediaMetadata(
        AudioStreamMetadata[] audioStreams,
        TimeSpan? duration)
    {
        AudioStreams = new ReadOnlyCollection<AudioStreamMetadata>(audioStreams);
        Duration = duration;
    }

    public IReadOnlyList<AudioStreamMetadata> AudioStreams { get; }

    public AudioStreamMetadata PrimaryAudioStream => AudioStreams[0];

    public TimeSpan? Duration { get; }
}

public enum MediaInspectionError
{
    ToolUnavailable,
    InvalidOrUnsupportedMedia,
    NoAudioStream,
    InvalidProbeResponse,
    TimedOut,
}

public sealed class MediaInspectionException : Exception
{
    internal MediaInspectionException(MediaInspectionError error)
        : base(MessageFor(error))
    {
        Error = error;
    }

    public MediaInspectionError Error { get; }

    private static string MessageFor(MediaInspectionError error) => error switch
    {
        MediaInspectionError.ToolUnavailable =>
            "Не удалось найти или запустить компонент проверки медиа.",
        MediaInspectionError.InvalidOrUnsupportedMedia =>
            "Файл повреждён или его медиаконтейнер не поддерживается.",
        MediaInspectionError.NoAudioStream =>
            "В выбранном файле не найден аудиопоток.",
        MediaInspectionError.InvalidProbeResponse =>
            "Компонент проверки медиа вернул некорректные данные.",
        MediaInspectionError.TimedOut =>
            "Проверка медиа заняла слишком много времени.",
        _ => "Не удалось проверить медиафайл.",
    };
}

public enum MediaConversionError
{
    ToolUnavailable,
    InvalidInput,
    InvalidDestination,
    OutputAlreadyExists,
    ProcessingFailed,
    TimedOut,
}

public sealed class MediaConversionException : Exception
{
    internal MediaConversionException(MediaConversionError error)
        : base(MessageFor(error))
    {
        Error = error;
    }

    public MediaConversionError Error { get; }

    private static string MessageFor(MediaConversionError error) => error switch
    {
        MediaConversionError.ToolUnavailable =>
            "Не удалось найти или запустить компонент обработки медиа.",
        MediaConversionError.InvalidInput =>
            "Входной медиафайл недоступен для чтения.",
        MediaConversionError.InvalidDestination =>
            "Не удалось подготовить папку результатов.",
        MediaConversionError.OutputAlreadyExists =>
            "Результат с таким именем уже существует.",
        MediaConversionError.ProcessingFailed =>
            "Не удалось извлечь аудио из выбранного файла.",
        MediaConversionError.TimedOut =>
            "Обработка медиа заняла слишком много времени.",
        _ => "Не удалось обработать медиафайл.",
    };
}
