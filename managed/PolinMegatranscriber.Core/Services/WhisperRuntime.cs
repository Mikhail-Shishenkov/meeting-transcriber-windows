namespace PolinMegatranscriber.Core;

internal interface IWhisperRuntime
{
    WhisperRuntimeResult Transcribe(
        TranscriptionRequest request,
        Action<double> progress,
        CancellationToken cancellationToken);
}

internal sealed record WhisperRuntimeSegment(
    long StartMilliseconds,
    long EndMilliseconds,
    string Text);

internal sealed record WhisperRuntimeResult(
    IReadOnlyList<WhisperRuntimeSegment> Segments,
    string? DetectedLanguage);

internal enum WhisperRuntimeError
{
    RuntimeUnavailable,
    InvalidModel,
    InvalidWav,
    UnsupportedWav,
    InferenceFailed,
    InvalidResult,
}

internal sealed class WhisperRuntimeException : Exception
{
    internal WhisperRuntimeException(
        WhisperRuntimeError error,
        Exception? innerException = null)
        : base(error.ToString(), innerException)
    {
        Error = error;
    }

    internal WhisperRuntimeError Error { get; }
}
