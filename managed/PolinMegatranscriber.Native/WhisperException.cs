namespace PolinMegatranscriber.Native;

/// <summary>
/// Reports a failure returned by the native Whisper bridge.
/// </summary>
public sealed class WhisperException : Exception
{
    public WhisperException(WhisperStatus status)
        : base(MessageFor(status))
    {
        Status = status;
    }

    public WhisperStatus Status { get; }

    private static string MessageFor(WhisperStatus status) => status switch
    {
        WhisperStatus.InvalidArgument => "The native Whisper bridge rejected an argument.",
        WhisperStatus.RuntimeUnavailable => "The local Whisper runtime is unavailable.",
        WhisperStatus.ModelLoadFailed => "The Whisper model could not be loaded.",
        WhisperStatus.InvalidWav => "The WAV file is invalid or unreadable.",
        WhisperStatus.UnsupportedWav => "The WAV file must be mono 16 kHz PCM16.",
        WhisperStatus.InferenceFailed => "Whisper inference failed.",
        WhisperStatus.InvalidResult => "The native Whisper result is invalid.",
        WhisperStatus.Cancelled => "Whisper transcription was cancelled.",
        _ => $"The native Whisper bridge returned unknown status {(int)status}.",
    };
}
