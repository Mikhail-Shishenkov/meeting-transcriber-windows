using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PolinMegatranscriber.Native;

/// <summary>
/// Private P/Invoke surface for the existing pmtwhisper.dll C API.
/// </summary>
internal static partial class PmtWhisperNative
{
    internal const string DllName = "pmtwhisper.dll";

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_runtime_available",
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int PmtWhisperRuntimeAvailable();

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_session_create",
        StringMarshalling = StringMarshalling.Utf8,
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial WhisperStatus PmtWhisperSessionCreate(
        string modelPath,
        out nint session);

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_session_destroy",
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void PmtWhisperSessionDestroy(nint session);

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_session_request_cancel",
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void PmtWhisperSessionRequestCancel(
        SafeWhisperSessionHandle session);

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_session_transcribe_wav",
        StringMarshalling = StringMarshalling.Utf8,
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial WhisperStatus PmtWhisperSessionTranscribeWav(
        SafeWhisperSessionHandle session,
        string wavPath,
        string language,
        int threadCount,
        nint progressCallback,
        nint progressUserData);

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_session_segment_count",
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint PmtWhisperSessionSegmentCount(
        SafeWhisperSessionHandle session);

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_session_segment_start_milliseconds",
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial long PmtWhisperSessionSegmentStartMilliseconds(
        SafeWhisperSessionHandle session,
        nuint index);

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_session_segment_end_milliseconds",
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial long PmtWhisperSessionSegmentEndMilliseconds(
        SafeWhisperSessionHandle session,
        nuint index);

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_session_segment_text",
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint PmtWhisperSessionSegmentText(
        SafeWhisperSessionHandle session,
        nuint index);

    [LibraryImport(
        DllName,
        EntryPoint = "pmt_whisper_session_detected_language",
        SetLastError = false)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint PmtWhisperSessionDetectedLanguage(
        SafeWhisperSessionHandle session);
}
