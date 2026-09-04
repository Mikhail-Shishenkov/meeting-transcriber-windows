using System.Security;

namespace PolinMegatranscriber.Core;

public interface IWhisperTranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class WhisperTranscriptionService : IWhisperTranscriptionService
{
    private readonly IWhisperRuntime runtime;
    private int inferenceIsRunning;

    public WhisperTranscriptionService()
        : this(new NativeWhisperRuntime())
    {
    }

    internal WhisperTranscriptionService(IWhisperRuntime runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(
                ref inferenceIsRunning,
                1,
                0) != 0)
        {
            return Task.FromException<TranscriptionResult>(
                new TranscriptionException(
                    TranscriptionError.InferenceInProgress));
        }

        return ExecuteAsync(request, progress, cancellationToken);
    }

    private async Task<TranscriptionResult> ExecuteAsync(
        TranscriptionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                    () => Execute(request, progress, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref inferenceIsRunning, 0);
        }
    }

    private TranscriptionResult Execute(
        TranscriptionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ValidateRegularReadableFile(
            request.ModelPath,
            TranscriptionError.InvalidModel);
        ValidateRegularReadableFile(
            request.WavPath,
            TranscriptionError.InvalidWav);
        cancellationToken.ThrowIfCancellationRequested();

        var relay = new TranscriptionProgressRelay(progress);
        WhisperRuntimeResult runtimeResult;
        try
        {
            runtimeResult = runtime.Transcribe(
                request,
                relay.Publish,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (WhisperRuntimeException exception)
        {
            throw new TranscriptionException(
                MapRuntimeError(exception.Error));
        }
        catch (TranscriptionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TranscriptionException(
                TranscriptionError.InferenceFailed,
                exception);
        }

        TranscriptionResult result = MakeResult(runtimeResult);
        relay.Finish();
        return result;
    }

    private static void ValidateRegularReadableFile(
        string path,
        TranscriptionError error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new TranscriptionException(error);
        }

        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists
                || (info.Attributes & FileAttributes.Directory) != 0
                || info.Length <= 0)
            {
                throw new TranscriptionException(error);
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (!stream.CanRead)
            {
                throw new TranscriptionException(error);
            }
        }
        catch (TranscriptionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SecurityException
                or NotSupportedException
                or ArgumentException)
        {
            throw new TranscriptionException(error, exception);
        }
    }

    private static TranscriptionResult MakeResult(
        WhisperRuntimeResult runtimeResult)
    {
        var segments = new TranscriptionSegment[runtimeResult.Segments.Count];
        for (int index = 0; index < segments.Length; index++)
        {
            WhisperRuntimeSegment source = runtimeResult.Segments[index];
            if (source.StartMilliseconds < 0
                || source.EndMilliseconds < source.StartMilliseconds
                || source.Text is null)
            {
                throw new TranscriptionException(
                    TranscriptionError.InvalidResult);
            }

            segments[index] = new TranscriptionSegment(
                source.StartMilliseconds,
                source.EndMilliseconds,
                source.Text);
        }

        return new TranscriptionResult(
            segments,
            runtimeResult.DetectedLanguage);
    }

    private static TranscriptionError MapRuntimeError(
        WhisperRuntimeError error) => error switch
        {
            WhisperRuntimeError.RuntimeUnavailable =>
                TranscriptionError.RuntimeUnavailable,
            WhisperRuntimeError.InvalidModel => TranscriptionError.InvalidModel,
            WhisperRuntimeError.InvalidWav => TranscriptionError.InvalidWav,
            WhisperRuntimeError.UnsupportedWav =>
                TranscriptionError.UnsupportedWav,
            WhisperRuntimeError.InvalidResult => TranscriptionError.InvalidResult,
            _ => TranscriptionError.InferenceFailed,
        };

    private sealed class TranscriptionProgressRelay
    {
        private readonly object gate = new();
        private readonly IProgress<double>? progress;
        private double lastValue;
        private bool finished;

        internal TranscriptionProgressRelay(IProgress<double>? progress)
        {
            this.progress = progress;
        }

        internal void Publish(double value)
        {
            double normalized;
            lock (gate)
            {
                if (finished || !double.IsFinite(value))
                {
                    return;
                }

                normalized = Math.Clamp(value, lastValue, 1.0);
                if (normalized <= lastValue)
                {
                    return;
                }

                lastValue = normalized;
            }

            ReportWithoutAffectingInference(normalized);
        }

        internal void Finish()
        {
            lock (gate)
            {
                if (finished)
                {
                    return;
                }

                finished = true;
                if (lastValue >= 1.0)
                {
                    return;
                }

                lastValue = 1.0;
            }

            ReportWithoutAffectingInference(1.0);
        }

        private void ReportWithoutAffectingInference(double value)
        {
            try
            {
                progress?.Report(value);
            }
            catch
            {
                // Progress is observational and cannot fail native inference.
            }
        }
    }
}
