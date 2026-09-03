using System.Runtime.InteropServices;
using PolinMegatranscriber.Native;

try
{
    bool available = WhisperRuntimeAvailability.IsRuntimeAvailable();
    Console.WriteLine(
        available
            ? "Smoke OK: pmt_whisper_runtime_available() reports the whisper.cpp runtime as available."
            : "Smoke FAILED: pmt_whisper_runtime_available() reports the runtime as unavailable.");
    return available ? 0 : 1;
}
catch (Exception ex) when (
    ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
{
    Console.WriteLine($"Smoke FAILED: could not call pmtwhisper.dll: {ex.Message}");
    return 2;
}
