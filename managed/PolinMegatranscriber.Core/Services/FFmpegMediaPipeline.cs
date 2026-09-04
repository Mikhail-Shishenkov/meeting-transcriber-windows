namespace PolinMegatranscriber.Core;

public interface IMediaConversionService
{
    Task<string> ConvertToMp3Async(
        string inputPath,
        string outputPath,
        TimeSpan? duration = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> ConvertToWhisperWavAsync(
        string inputPath,
        string outputPath,
        TimeSpan? duration = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class FFmpegMediaPipeline : IMediaConversionService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(2);

    private readonly IMediaToolLocator toolLocator;
    private readonly IProcessRunner processRunner;
    private readonly TimeSpan timeout;

    public FFmpegMediaPipeline(
        IMediaToolLocator toolLocator,
        TimeSpan? timeout = null)
        : this(toolLocator, new WindowsProcessRunner(), timeout)
    {
    }

    internal FFmpegMediaPipeline(
        IMediaToolLocator toolLocator,
        IProcessRunner processRunner,
        TimeSpan? timeout = null)
    {
        this.toolLocator = toolLocator
            ?? throw new ArgumentNullException(nameof(toolLocator));
        this.processRunner = processRunner
            ?? throw new ArgumentNullException(nameof(processRunner));
        this.timeout = ValidateTimeout(timeout ?? DefaultTimeout);
    }

    public Task<string> ConvertToMp3Async(
        string inputPath,
        string outputPath,
        TimeSpan? duration = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        ConvertAsync(
            inputPath,
            outputPath,
            duration,
            progress,
            cancellationToken,
            MediaOutputKind.Mp3);

    public Task<string> ConvertToWhisperWavAsync(
        string inputPath,
        string outputPath,
        TimeSpan? duration = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        ConvertAsync(
            inputPath,
            outputPath,
            duration,
            progress,
            cancellationToken,
            MediaOutputKind.WhisperWav);

    private async Task<string> ConvertAsync(
        string inputPath,
        string outputPath,
        TimeSpan? duration,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        MediaOutputKind outputKind)
    {
        ValidateInput(inputPath);
        string outputFullPath = ValidateDestination(outputPath);
        if (File.Exists(outputFullPath) || Directory.Exists(outputFullPath))
        {
            throw new MediaConversionException(
                MediaConversionError.OutputAlreadyExists);
        }

        string ffmpegPath;
        try
        {
            ffmpegPath = toolLocator.Locate().FFmpegPath;
        }
        catch
        {
            throw new MediaConversionException(
                MediaConversionError.ToolUnavailable);
        }

        string stagingPath = CreateOwnedStagingPath(outputFullPath);
        var progressRelay = new MediaProgressRelay(progress);
        var progressReader = new FFmpegProgressStreamReader(
            duration,
            progressEvent => progressRelay.Report(progressEvent.Fraction));
        try
        {
            ProcessResult result;
            try
            {
                result = await processRunner.RunAsync(
                        new ProcessRequest(
                            ffmpegPath,
                            BuildArguments(
                                inputPath,
                                stagingPath,
                                outputKind),
                            timeout),
                        chunk =>
                        {
                            progressReader.Consume(chunk);
                            return ValueTask.CompletedTask;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (ProcessRunnerException exception)
                when (exception.Error == ProcessRunnerError.TimedOut)
            {
                throw new MediaConversionException(
                    MediaConversionError.TimedOut);
            }
        catch (ProcessRunnerException exception)
            when (exception.Error is ProcessRunnerError.ExecutableUnavailable
                    or ProcessRunnerError.LaunchFailed
                    or ProcessRunnerError.InvalidRequest)
            {
                throw new MediaConversionException(
                    MediaConversionError.ToolUnavailable);
            }
            catch (ProcessRunnerException)
            {
                throw new MediaConversionException(
                    MediaConversionError.ProcessingFailed);
            }

            progressReader.Complete();
            if (result.ExitCode != 0 || !IsNonEmptyRegularFile(stagingPath))
            {
                throw new MediaConversionException(
                    MediaConversionError.ProcessingFailed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(stagingPath, outputFullPath, overwrite: false);
            }
            catch (IOException) when (
                File.Exists(outputFullPath)
                || Directory.Exists(outputFullPath))
            {
                throw new MediaConversionException(
                    MediaConversionError.OutputAlreadyExists);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {
                throw new MediaConversionException(
                    MediaConversionError.InvalidDestination);
            }

            progressRelay.Finish();
            return outputFullPath;
        }
        finally
        {
            DeleteOwnedStaging(stagingPath);
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        string inputPath,
        string stagingPath,
        MediaOutputKind outputKind)
    {
        var arguments = new List<string>
        {
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-progress", "pipe:1",
            "-nostats",
            "-n",
            "-i", inputPath,
            "-map", "0:a:0",
            "-vn",
        };
        if (outputKind == MediaOutputKind.Mp3)
        {
            arguments.AddRange(["-c:a", "libmp3lame", "-f", "mp3"]);
        }
        else
        {
            arguments.AddRange(
                [
                    "-ac", "1",
                    "-ar", "16000",
                    "-c:a", "pcm_s16le",
                    "-f", "wav",
                ]);
        }

        arguments.Add(stagingPath);
        return arguments;
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return timeout;
    }

    private static string CreateOwnedStagingPath(string outputFullPath)
    {
        string directory = Path.GetDirectoryName(outputFullPath)!;
        string fileName = Path.GetFileName(outputFullPath);
        return Path.Combine(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.partial");
    }

    private static string ValidateDestination(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new MediaConversionException(
                MediaConversionError.InvalidDestination);
        }

        try
        {
            string fullPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory)
                || !Directory.Exists(directory))
            {
                throw new MediaConversionException(
                    MediaConversionError.InvalidDestination);
            }

            return fullPath;
        }
        catch (MediaConversionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            throw new MediaConversionException(
                MediaConversionError.InvalidDestination);
        }
    }

    private static void ValidateInput(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new MediaConversionException(MediaConversionError.InvalidInput);
        }

        try
        {
            var file = new FileInfo(inputPath);
            if (!file.Exists
                || (file.Attributes & FileAttributes.Directory) != 0
                || file.Length <= 0)
            {
                throw new MediaConversionException(
                    MediaConversionError.InvalidInput);
            }

            using var stream = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (MediaConversionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            throw new MediaConversionException(MediaConversionError.InvalidInput);
        }
    }

    private static bool IsNonEmptyRegularFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists
                && (file.Attributes & FileAttributes.Directory) == 0
                && file.Length > 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static void DeleteOwnedStaging(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
        }
    }

    private enum MediaOutputKind
    {
        Mp3,
        WhisperWav,
    }

    private sealed class MediaProgressRelay
    {
        private readonly object gate = new();
        private readonly IProgress<double>? progress;
        private double fraction;
        private bool finished;

        internal MediaProgressRelay(IProgress<double>? progress)
        {
            this.progress = progress;
        }

        internal void Report(double candidate)
        {
            double value;
            lock (gate)
            {
                if (finished || !double.IsFinite(candidate))
                {
                    return;
                }

                value = Math.Clamp(candidate, fraction, 1.0);
                if (value <= fraction)
                {
                    return;
                }

                fraction = value;
            }

            ReportSafely(value);
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
                if (fraction >= 1.0)
                {
                    return;
                }

                fraction = 1.0;
            }

            ReportSafely(1.0);
        }

        private void ReportSafely(double value)
        {
            try
            {
                progress?.Report(value);
            }
            catch
            {
                // Progress is observational and cannot fail media processing.
            }
        }
    }
}
