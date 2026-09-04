using PolinMegatranscriber.Native;

namespace PolinMegatranscriber.Core;

internal sealed class NativeWhisperRuntime : IWhisperRuntime
{
    private readonly int? threadCount;

    internal NativeWhisperRuntime(int? threadCount = null)
    {
        if (threadCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threadCount));
        }

        this.threadCount = threadCount;
    }

    public WhisperRuntimeResult Transcribe(
        TranscriptionRequest request,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!WhisperRuntimeAvailability.IsRuntimeAvailable())
            {
                throw new WhisperRuntimeException(
                    WhisperRuntimeError.RuntimeUnavailable);
            }

            using WhisperSession session = WhisperSession.Create(
                request.ModelPath);
            using CancellationTokenRegistration registration =
                cancellationToken.UnsafeRegister(
                    static state => RequestCancellation(state),
                    session);

            WhisperTranscriptionResult nativeResult = session.TranscribeWav(
                request.WavPath,
                request.Language.ToBridgeCode(),
                threadCount,
                value => progress(value));
            cancellationToken.ThrowIfCancellationRequested();

            var segments = new WhisperRuntimeSegment[
                nativeResult.Segments.Count];
            for (int index = 0; index < segments.Length; index++)
            {
                WhisperSegment segment = nativeResult.Segments[index];
                segments[index] = new WhisperRuntimeSegment(
                    segment.StartMilliseconds,
                    segment.EndMilliseconds,
                    segment.Text);
            }

            return new WhisperRuntimeResult(
                segments,
                nativeResult.DetectedLanguage);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (WhisperException exception)
        {
            throw new WhisperRuntimeException(
                MapStatus(exception.Status),
                exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new WhisperRuntimeException(
                WhisperRuntimeError.InferenceFailed,
                exception);
        }
    }

    private static void RequestCancellation(object? state)
    {
        try
        {
            ((WhisperSession)state!).RequestCancellation();
        }
        catch (ObjectDisposedException)
        {
            // Disposal can win a cancellation race after native inference.
        }
    }

    private static WhisperRuntimeError MapStatus(WhisperStatus status) =>
        status switch
        {
            WhisperStatus.RuntimeUnavailable =>
                WhisperRuntimeError.RuntimeUnavailable,
            WhisperStatus.ModelLoadFailed => WhisperRuntimeError.InvalidModel,
            WhisperStatus.InvalidWav => WhisperRuntimeError.InvalidWav,
            WhisperStatus.UnsupportedWav => WhisperRuntimeError.UnsupportedWav,
            WhisperStatus.InvalidResult => WhisperRuntimeError.InvalidResult,
            _ => WhisperRuntimeError.InferenceFailed,
        };
}

internal static class TranscriptionLanguageExtensions
{
    internal static string ToBridgeCode(this TranscriptionLanguage language) =>
        language switch
        {
            TranscriptionLanguage.Automatic => "auto",
            TranscriptionLanguage.Russian => "ru",
            _ => throw new ArgumentOutOfRangeException(nameof(language)),
        };
}
