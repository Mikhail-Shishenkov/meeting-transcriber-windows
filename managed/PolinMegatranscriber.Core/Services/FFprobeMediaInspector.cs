using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolinMegatranscriber.Core;

public interface IMediaInspector
{
    Task<MediaMetadata> InspectAsync(
        string inputPath,
        CancellationToken cancellationToken = default);
}

public sealed class FFprobeMediaInspector : IMediaInspector
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IMediaToolLocator toolLocator;
    private readonly IProcessRunner processRunner;
    private readonly TimeSpan timeout;

    public FFprobeMediaInspector(
        IMediaToolLocator toolLocator,
        TimeSpan? timeout = null)
        : this(toolLocator, new WindowsProcessRunner(), timeout)
    {
    }

    internal FFprobeMediaInspector(
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

    public async Task<MediaMetadata> InspectAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(inputPath);
        cancellationToken.ThrowIfCancellationRequested();

        string ffprobePath;
        try
        {
            ffprobePath = toolLocator.Locate().FFprobePath;
        }
        catch
        {
            throw new MediaInspectionException(
                MediaInspectionError.ToolUnavailable);
        }

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                    new ProcessRequest(
                        ffprobePath,
                        [
                            "-v", "error",
                            "-select_streams", "a",
                            "-show_entries",
                            "stream=index,codec_name,sample_rate,channels,duration:format=duration",
                            "-of", "json",
                            inputPath,
                        ],
                        timeout),
                    cancellationToken: cancellationToken)
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
            throw new MediaInspectionException(MediaInspectionError.TimedOut);
        }
        catch (ProcessRunnerException exception)
            when (exception.Error is ProcessRunnerError.ExecutableUnavailable
                or ProcessRunnerError.LaunchFailed
                or ProcessRunnerError.InvalidRequest)
        {
            throw new MediaInspectionException(
                MediaInspectionError.ToolUnavailable);
        }
        catch (ProcessRunnerException)
        {
            throw new MediaInspectionException(
                MediaInspectionError.InvalidProbeResponse);
        }

        if (result.ExitCode != 0)
        {
            throw new MediaInspectionException(
                MediaInspectionError.InvalidOrUnsupportedMedia);
        }
        if (result.StandardOutputWasTruncated)
        {
            throw new MediaInspectionException(
                MediaInspectionError.InvalidProbeResponse);
        }

        ProbeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<ProbeResponse>(
                result.StandardOutput);
        }
        catch (JsonException)
        {
            throw new MediaInspectionException(
                MediaInspectionError.InvalidProbeResponse);
        }

        if (response is null)
        {
            throw new MediaInspectionException(
                MediaInspectionError.InvalidProbeResponse);
        }
        if (response.Streams is null)
        {
            throw new MediaInspectionException(
                MediaInspectionError.InvalidProbeResponse);
        }
        if (response.Streams.Count == 0)
        {
            throw new MediaInspectionException(
                MediaInspectionError.NoAudioStream);
        }

        var streams = new AudioStreamMetadata[response.Streams.Count];
        for (int index = 0; index < streams.Length; index++)
        {
            ProbeStream? source = response.Streams[index];
            if (source is null
                || source.Index is not >= 0
                || string.IsNullOrWhiteSpace(source.CodecName)
                || !TryPositiveInteger(source.SampleRate, out int sampleRate)
                || source.Channels is not > 0)
            {
                throw new MediaInspectionException(
                    MediaInspectionError.InvalidProbeResponse);
            }

            streams[index] = new AudioStreamMetadata(
                source.Index.Value,
                source.CodecName,
                sampleRate,
                source.Channels.Value,
                ParseDuration(source.Duration));
        }

        TimeSpan? duration = ParseDuration(response.Format?.Duration)
            ?? streams.Select(stream => stream.Duration)
                .FirstOrDefault(candidate => candidate is not null);
        return new MediaMetadata(streams, duration);
    }

    private static void ValidateInput(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new MediaInspectionException(
                MediaInspectionError.InvalidOrUnsupportedMedia);
        }

        try
        {
            var file = new FileInfo(inputPath);
            if (!file.Exists
                || (file.Attributes & FileAttributes.Directory) != 0
                || file.Length <= 0)
            {
                throw new MediaInspectionException(
                    MediaInspectionError.InvalidOrUnsupportedMedia);
            }

            using var stream = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (MediaInspectionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            throw new MediaInspectionException(
                MediaInspectionError.InvalidOrUnsupportedMedia);
        }
    }

    private static bool TryPositiveInteger(string? value, out int result) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out result)
        && result > 0;

    private static TimeSpan? ParseDuration(string? value)
    {
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double seconds)
            || !double.IsFinite(seconds)
            || seconds <= 0
            || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return timeout;
    }

    private sealed class ProbeResponse
    {
        [JsonPropertyName("streams")]
        public List<ProbeStream?>? Streams { get; init; } = [];

        [JsonPropertyName("format")]
        public ProbeFormat? Format { get; init; }
    }

    private sealed class ProbeStream
    {
        [JsonPropertyName("index")]
        public int? Index { get; init; }

        [JsonPropertyName("codec_name")]
        public string? CodecName { get; init; }

        [JsonPropertyName("sample_rate")]
        public string? SampleRate { get; init; }

        [JsonPropertyName("channels")]
        public int? Channels { get; init; }

        [JsonPropertyName("duration")]
        public string? Duration { get; init; }
    }

    private sealed class ProbeFormat
    {
        [JsonPropertyName("duration")]
        public string? Duration { get; init; }
    }
}
