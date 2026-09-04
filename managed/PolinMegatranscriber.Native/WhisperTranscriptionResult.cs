using System.Collections.ObjectModel;

namespace PolinMegatranscriber.Native;

/// <summary>
/// One timestamped UTF-8 transcript segment.
/// </summary>
public sealed record WhisperSegment(
    long StartMilliseconds,
    long EndMilliseconds,
    string Text);

/// <summary>
/// Complete data copied from a native session after successful inference.
/// </summary>
public sealed class WhisperTranscriptionResult
{
    internal WhisperTranscriptionResult(
        WhisperSegment[] segments,
        string? detectedLanguage)
    {
        Segments = new ReadOnlyCollection<WhisperSegment>(segments);
        DetectedLanguage = detectedLanguage;
    }

    public IReadOnlyList<WhisperSegment> Segments { get; }

    public string? DetectedLanguage { get; }
}
