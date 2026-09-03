using System.Runtime.InteropServices;

namespace PolinMegatranscriber.Native;

/// <summary>
/// Minimal P/Invoke interop for the existing pmtwhisper.dll native C API.
/// Only <c>pmt_whisper_runtime_available</c> is wrapped so far (TASK-003).
/// </summary>
internal static partial class PmtWhisperNative
{
    internal const string DllName = "pmtwhisper.dll";

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_runtime_available",
        SetLastError = false)]
    internal static partial int PmtWhisperRuntimeAvailable();
}
