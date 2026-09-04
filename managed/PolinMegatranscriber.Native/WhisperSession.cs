using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace PolinMegatranscriber.Native;

/// <summary>
/// Owns one native Whisper context and provides the managed transcription API.
/// </summary>
public sealed class WhisperSession : IDisposable
{
    private const int MaximumDefaultThreadCount = 8;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeProgressCallback(float progress, nint userData);

    private static readonly NativeProgressCallback ProgressCallback = ReportProgress;
    private static readonly nint ProgressCallbackPointer =
        Marshal.GetFunctionPointerForDelegate(ProgressCallback);

    private readonly object operationGate = new();
    private readonly SafeWhisperSessionHandle handle;
    private bool disposed;

    private WhisperSession(SafeWhisperSessionHandle handle)
    {
        this.handle = handle;
    }

    /// <summary>
    /// Loads a model and creates an owned native session.
    /// </summary>
    public static WhisperSession Create(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        WhisperStatus status = PmtWhisperNative.PmtWhisperSessionCreate(
            modelPath,
            out nint session);
        if (status != WhisperStatus.Ok)
        {
            if (session != nint.Zero)
            {
                using var unexpectedHandle = new SafeWhisperSessionHandle(session);
            }

            throw new WhisperException(status);
        }

        if (session == nint.Zero)
        {
            throw new WhisperException(WhisperStatus.InvalidResult);
        }

        return new WhisperSession(new SafeWhisperSessionHandle(session));
    }

    /// <summary>
    /// Transcribes a mono 16 kHz PCM16 WAV and copies all borrowed native data
    /// into an immutable managed result before returning.
    /// </summary>
    public WhisperTranscriptionResult TranscribeWav(
        string wavPath,
        string language = "auto",
        int? threadCount = null,
        Action<float>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        int effectiveThreadCount = threadCount
            ?? Math.Clamp(Environment.ProcessorCount, 1, MaximumDefaultThreadCount);
        if (effectiveThreadCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threadCount),
                "Thread count must be at least one.");
        }

        lock (operationGate)
        {
            ThrowIfDisposed();
            var progressState = progress is null
                ? null
                : new ProgressState(progress);
            GCHandle progressStateHandle = default;
            nint progressUserData = nint.Zero;
            if (progressState is not null)
            {
                progressStateHandle = GCHandle.Alloc(progressState);
                progressUserData = GCHandle.ToIntPtr(progressStateHandle);
            }

            try
            {
                WhisperStatus status =
                    PmtWhisperNative.PmtWhisperSessionTranscribeWav(
                        handle,
                        wavPath,
                        language,
                        effectiveThreadCount,
                        progressState is null
                            ? nint.Zero
                            : ProgressCallbackPointer,
                        progressUserData);
                GC.KeepAlive(ProgressCallback);

                if (status == WhisperStatus.Cancelled)
                {
                    throw new OperationCanceledException(
                        "Whisper transcription was cancelled.");
                }

                if (status != WhisperStatus.Ok)
                {
                    throw new WhisperException(status);
                }

                progressState?.ThrowIfCallbackFailed();
                return ReadResult();
            }
            finally
            {
                if (progressStateHandle.IsAllocated)
                {
                    progressStateHandle.Free();
                }
            }
        }
    }

    /// <summary>
    /// Requests cancellation. This method is safe to call from a different
    /// thread while <see cref="TranscribeWav"/> is running.
    /// </summary>
    public void RequestCancellation()
    {
        ThrowIfDisposed();
        PmtWhisperNative.PmtWhisperSessionRequestCancel(handle);
    }

    public void Dispose()
    {
        lock (operationGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            handle.Dispose();
        }
    }

    private WhisperTranscriptionResult ReadResult()
    {
        nuint nativeCount =
            PmtWhisperNative.PmtWhisperSessionSegmentCount(handle);
        if (nativeCount > int.MaxValue)
        {
            throw new WhisperException(WhisperStatus.InvalidResult);
        }

        var segments = new WhisperSegment[(int)nativeCount];
        for (nuint index = 0; index < nativeCount; index++)
        {
            long start =
                PmtWhisperNative.PmtWhisperSessionSegmentStartMilliseconds(
                    handle,
                    index);
            long end =
                PmtWhisperNative.PmtWhisperSessionSegmentEndMilliseconds(
                    handle,
                    index);
            nint textPointer =
                PmtWhisperNative.PmtWhisperSessionSegmentText(handle, index);
            if (start < 0 || end < start || textPointer == nint.Zero)
            {
                throw new WhisperException(WhisperStatus.InvalidResult);
            }

            string? text = Marshal.PtrToStringUTF8(textPointer);
            if (text is null)
            {
                throw new WhisperException(WhisperStatus.InvalidResult);
            }

            segments[(int)index] = new WhisperSegment(start, end, text);
        }

        nint languagePointer =
            PmtWhisperNative.PmtWhisperSessionDetectedLanguage(handle);
        string? detectedLanguage = languagePointer == nint.Zero
            ? null
            : Marshal.PtrToStringUTF8(languagePointer);
        return new WhisperTranscriptionResult(segments, detectedLanguage);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed || handle.IsClosed, this);
    }

    private static void ReportProgress(float progress, nint userData)
    {
        if (userData == nint.Zero)
        {
            return;
        }

        try
        {
            if (GCHandle.FromIntPtr(userData).Target is ProgressState state)
            {
                state.Report(progress);
            }
        }
        catch
        {
            // No managed exception may cross the unmanaged callback boundary.
        }
    }

    private sealed class ProgressState
    {
        private readonly Action<float> callback;
        private ExceptionDispatchInfo? callbackFailure;
        private float lastValue;

        internal ProgressState(Action<float> callback)
        {
            this.callback = callback;
        }

        internal void Report(float value)
        {
            if (!float.IsFinite(value) || callbackFailure is not null)
            {
                return;
            }

            float normalized = Math.Clamp(value, lastValue, 1.0F);
            if (normalized <= lastValue)
            {
                return;
            }

            lastValue = normalized;
            try
            {
                callback(normalized);
            }
            catch (Exception exception)
            {
                callbackFailure = ExceptionDispatchInfo.Capture(exception);
            }
        }

        internal void ThrowIfCallbackFailed() => callbackFailure?.Throw();
    }
}
