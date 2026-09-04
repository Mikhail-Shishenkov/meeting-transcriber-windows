namespace PolinMegatranscriber.Native;

/// <summary>
/// Status values defined by PolinWhisperBridge.h.
/// </summary>
public enum WhisperStatus
{
    Ok = 0,
    InvalidArgument = 1,
    RuntimeUnavailable = 2,
    ModelLoadFailed = 3,
    InvalidWav = 4,
    UnsupportedWav = 5,
    InferenceFailed = 6,
    Cancelled = 7,
    InvalidResult = 8,
}
