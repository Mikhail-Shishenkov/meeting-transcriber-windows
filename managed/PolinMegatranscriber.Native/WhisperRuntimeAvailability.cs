namespace PolinMegatranscriber.Native;

/// <summary>
/// Managed wrapper around <c>pmt_whisper_runtime_available()</c> from pmtwhisper.dll.
/// The native function returns 1 when the whisper.cpp runtime is compiled in,
/// and 0 otherwise.
/// </summary>
public static class WhisperRuntimeAvailability
{
    /// <summary>
    /// Returns true when the native runtime reports itself available.
    /// Throws <see cref="DllNotFoundException"/> when pmtwhisper.dll cannot be loaded,
    /// or <see cref="EntryPointNotFoundException"/> when the export is missing.
    /// </summary>
    public static bool IsRuntimeAvailable() =>
        PmtWhisperNative.PmtWhisperRuntimeAvailable() != 0;
}
