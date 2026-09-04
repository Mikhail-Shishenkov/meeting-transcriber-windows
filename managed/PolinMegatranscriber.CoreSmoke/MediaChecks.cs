using System.Diagnostics;
using System.Reflection;
using System.Text;
using PolinMegatranscriber.Core;

internal static partial class CoreSmoke
{
    private static void VerifyFFmpegProgressParser()
    {
        var events = new List<FFmpegProgressEvent>();
        var reader = new FFmpegProgressStreamReader(
            TimeSpan.FromSeconds(10),
            events.Add);
        reader.Consume("out_time_");
        reader.Consume("us=2000000\r\nout_time_us=1000000\n");
        reader.Consume("out_time_us=NaN\nprogress=end");
        reader.Complete();

        Assert(events.Count == 3, "Unexpected FFmpeg progress event count.");
        Assert(
            events[0] == new FFmpegProgressEvent(
                FFmpegProgressEventKind.Fraction,
                0.2),
            "out_time_us was not parsed.");
        Assert(
            events[1].Fraction == 0.2,
            "FFmpeg progress regressed.");
        Assert(
            events[2] == new FFmpegProgressEvent(
                FFmpegProgressEventKind.End,
                1.0),
            "progress=end was not parsed.");
        Assert(
            events.All(item => double.IsFinite(item.Fraction)
                && item.Fraction is >= 0 and <= 1),
            "FFmpeg progress was not finite and bounded.");
    }

    private static async Task VerifyProcessRunnerContractAsync()
    {
        ProcessInvocation invocation = CurrentProcessInvocation();
        var runner = new WindowsProcessRunner(diagnosticLimit: 1_024);
        const string output =
            "C:\\Users\\Козявочка\\Путь с пробелами & ^ % !.wav";
        const string error = "stderr с кириллицей";
        ProcessResult echo = await runner.RunAsync(
            FixtureRequest(invocation, ["echo", output, error]));
        Assert(echo.ExitCode == 0, "Process fixture failed.");
        Assert(echo.StandardOutput == output, "ArgumentList changed Unicode argument.");
        Assert(echo.StandardError == error, "stderr was not drained.");

        ProcessResult diagnostic = await runner.RunAsync(
            FixtureRequest(invocation, ["diagnostic"]));
        Assert(
            diagnostic.StandardOutput.Length == 1_024
                && diagnostic.StandardOutputWasTruncated,
            "stdout diagnostic limit was not enforced.");
        Assert(
            diagnostic.StandardError.Length == 1_024
                && diagnostic.StandardErrorWasTruncated,
            "stderr diagnostic limit was not enforced.");

        await VerifyStoppedProcessAsync(invocation, timeout: false);
        await VerifyStoppedProcessAsync(invocation, timeout: true);
    }

    private static async Task VerifyStoppedProcessAsync(
        ProcessInvocation invocation,
        bool timeout)
    {
        var pidSource = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new StringBuilder();
        using var cancellation = new CancellationTokenSource();
        TimeSpan processTimeout = timeout
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromSeconds(10);
        var runner = new WindowsProcessRunner();
        Task<ProcessResult> execution = runner.RunAsync(
            FixtureRequest(invocation, ["wait"], processTimeout),
            chunk =>
            {
                pending.Append(chunk);
                string text = pending.ToString();
                int newline = text.IndexOf('\n');
                if (newline >= 0
                    && int.TryParse(text[..newline].Trim(), out int processId))
                {
                    pidSource.TrySetResult(processId);
                }

                return ValueTask.CompletedTask;
            },
            cancellation.Token);
        int pid = await pidSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout)
        {
            cancellation.Cancel();
        }

        if (timeout)
        {
            try
            {
                _ = await execution;
                throw new InvalidOperationException("Expected process timeout.");
            }
            catch (ProcessRunnerException exception)
                when (exception.Error == ProcessRunnerError.TimedOut)
            {
            }
        }
        else
        {
            try
            {
                _ = await execution;
                throw new InvalidOperationException("Expected process cancellation.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Task.Delay(100);
        Assert(!IsProcessRunning(pid), "Cancelled/timed-out process was left running.");
    }

    private static async Task VerifyMediaServiceContractsAsync()
    {
        using var inputs = MediaTestInputs.Create();
        const string probeJson = """
            {
              "streams": [
                {
                  "index": 1,
                  "codec_name": "aac",
                  "sample_rate": "48000",
                  "channels": 2,
                  "duration": "2.5"
                }
              ],
              "format": { "duration": "3.0" }
            }
            """;
        var probeRunner = new DelegateProcessRunner((request, _, _) =>
        {
            Assert(request.Arguments[^1] == inputs.InputPath,
                "FFprobe input path was changed.");
            Assert(request.Arguments.Contains("stream=index,codec_name,sample_rate,channels,duration:format=duration"),
                "FFprobe entries contract changed.");
            return Task.FromResult(new ProcessResult(
                0,
                probeJson,
                string.Empty,
                false,
                false));
        });
        var locator = new FixedMediaToolLocator("ffmpeg.exe", "ffprobe.exe");
        var inspector = new FFprobeMediaInspector(locator, probeRunner);
        MediaMetadata metadata = await inspector.InspectAsync(inputs.InputPath);
        Assert(metadata.AudioStreams.Count == 1, "Audio stream was not mapped.");
        Assert(metadata.PrimaryAudioStream.Index == 1, "Primary stream was not mapped.");
        Assert(metadata.PrimaryAudioStream.CodecName == "aac", "Codec was not mapped.");
        Assert(metadata.PrimaryAudioStream.SampleRateHz == 48_000,
            "Sample rate was not mapped.");
        Assert(metadata.PrimaryAudioStream.ChannelCount == 2,
            "Channel count was not mapped.");
        Assert(metadata.Duration == TimeSpan.FromSeconds(3),
            "Container duration was not mapped.");

        var noAudioRunner = new DelegateProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessResult(
                0,
                "{\"streams\":[]}",
                string.Empty,
                false,
                false)));
        await AssertInspectionErrorAsync(
            MediaInspectionError.NoAudioStream,
            () => new FFprobeMediaInspector(locator, noAudioRunner)
                .InspectAsync(inputs.InputPath));
        var invalidProbeRunner = new DelegateProcessRunner((_, _, _) =>
            Task.FromResult(new ProcessResult(
                0,
                "{\"streams\":null}",
                string.Empty,
                false,
                false)));
        await AssertInspectionErrorAsync(
            MediaInspectionError.InvalidProbeResponse,
            () => new FFprobeMediaInspector(locator, invalidProbeRunner)
                .InspectAsync(inputs.InputPath));

        var progress = new RecordingProgress();
        ProcessRequest? capturedRequest = null;
        var conversionRunner = new DelegateProcessRunner(
            async (request, outputHandler, _) =>
            {
                capturedRequest = request;
                File.WriteAllBytes(request.Arguments[^1], [1, 2, 3]);
                if (outputHandler is not null)
                {
                    await outputHandler("out_time_us=1500000\nprogress=end\n");
                }

                return new ProcessResult(0, string.Empty, string.Empty, false, false);
            });
        var pipeline = new FFmpegMediaPipeline(locator, conversionRunner);
        string published = await pipeline.ConvertToWhisperWavAsync(
            inputs.InputPath,
            inputs.OutputPath,
            TimeSpan.FromSeconds(3),
            progress);
        Assert(published == Path.GetFullPath(inputs.OutputPath),
            "Published output path changed.");
        Assert(File.Exists(published), "Owned staging result was not published.");
        ProcessRequest actualRequest = capturedRequest
            ?? throw new InvalidOperationException("FFmpeg was not invoked.");
        AssertWavArguments(actualRequest.Arguments, inputs.InputPath);
        AssertMonotonicFinished(progress.Values);

        long publishedLength = new FileInfo(published).Length;
        await AssertConversionErrorAsync(
            MediaConversionError.OutputAlreadyExists,
            () => pipeline.ConvertToWhisperWavAsync(
                inputs.InputPath,
                inputs.OutputPath));
        Assert(new FileInfo(published).Length == publishedLength,
            "Existing output was overwritten.");

        string callbackOutput = Path.Combine(inputs.Root, "callback.mp3");
        string callbackResult = await pipeline.ConvertToMp3Async(
            inputs.InputPath,
            callbackOutput,
            TimeSpan.FromSeconds(3),
            new ThrowingProgress());
        Assert(File.Exists(callbackResult),
            "Progress callback exception broke media processing.");

        string cancelledOutput = Path.Combine(inputs.Root, "cancelled.mp3");
        var partialCreated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRunner = new DelegateProcessRunner(
            async (request, _, cancellationToken) =>
            {
                File.WriteAllBytes(request.Arguments[^1], [9, 9, 9]);
                partialCreated.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable fake runner state.");
            });
        var cancellationPipeline = new FFmpegMediaPipeline(
            locator,
            cancellationRunner);
        using var cancellation = new CancellationTokenSource();
        Task<string> cancelled = cancellationPipeline.ConvertToMp3Async(
            inputs.InputPath,
            cancelledOutput,
            cancellationToken: cancellation.Token);
        await partialCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await AssertCancelledMediaAsync(cancelled);
        Assert(!File.Exists(cancelledOutput), "Cancelled output was published.");
        Assert(
            !Directory.EnumerateFiles(inputs.Root, "*.partial").Any(),
            "Owned partial output was not cleaned.");
    }

    private static async Task RunRealMediaAsync(MediaArguments arguments)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"TASK 006 Юникод и пробелы {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "Вход JFK с пробелами.wav");
            string mp3 = Path.Combine(root, "Результат MP3 с пробелами.mp3");
            string wav = Path.Combine(root, "Whisper WAV с кириллицей.wav");
            File.Copy(arguments.InputPath, input, overwrite: false);

            var locator = new FixedMediaToolLocator(
                arguments.FFmpegPath,
                arguments.FFprobePath);
            var inspector = new FFprobeMediaInspector(locator);
            var pipeline = new FFmpegMediaPipeline(locator);
            MediaMetadata inputMetadata = await inspector.InspectAsync(input);
            Assert(inputMetadata.AudioStreams.Count > 0,
                "Real FFprobe found no audio stream.");

            var mp3Progress = new RecordingProgress();
            var wavProgress = new RecordingProgress();
            await pipeline.ConvertToMp3Async(
                input,
                mp3,
                inputMetadata.Duration,
                mp3Progress);
            await pipeline.ConvertToWhisperWavAsync(
                input,
                wav,
                inputMetadata.Duration,
                wavProgress);
            Assert(new FileInfo(mp3).Length > 0, "Real MP3 is empty.");
            Assert(new FileInfo(wav).Length > 0, "Real Whisper WAV is empty.");
            AssertMonotonicFinished(mp3Progress.Values);
            AssertMonotonicFinished(wavProgress.Values);

            MediaMetadata mp3Metadata = await inspector.InspectAsync(mp3);
            MediaMetadata wavMetadata = await inspector.InspectAsync(wav);
            Assert(mp3Metadata.PrimaryAudioStream.CodecName == "mp3",
                "Real MP3 codec is not mp3.");
            Assert(wavMetadata.PrimaryAudioStream.CodecName == "pcm_s16le",
                "Whisper WAV codec is not PCM16.");
            Assert(wavMetadata.PrimaryAudioStream.SampleRateHz == 16_000,
                "Whisper WAV sample rate is not 16 kHz.");
            Assert(wavMetadata.PrimaryAudioStream.ChannelCount == 1,
                "Whisper WAV is not mono.");

            byte[] originalMp3 = File.ReadAllBytes(mp3);
            await AssertConversionErrorAsync(
                MediaConversionError.OutputAlreadyExists,
                () => pipeline.ConvertToMp3Async(input, mp3));
            Assert(File.ReadAllBytes(mp3).SequenceEqual(originalMp3),
                "Real existing MP3 was overwritten.");

            await VerifyRealFFmpegCancellationAsync(
                arguments.FFmpegPath,
                input);
            Console.WriteLine(
                $"Media real smoke OK: input={inputMetadata.PrimaryAudioStream.CodecName}, "
                + $"mp3={new FileInfo(mp3).Length} bytes, "
                + $"wav={new FileInfo(wav).Length} bytes, "
                + "Unicode paths/progress/cancellation passed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyRealFFmpegCancellationAsync(
        string ffmpegPath,
        string inputPath)
    {
        HashSet<int> before = Process.GetProcessesByName("ffmpeg")
            .Select(process => process.Id)
            .ToHashSet();
        using var cancellation = new CancellationTokenSource();
        var runner = new WindowsProcessRunner();
        Task<ProcessResult> execution = runner.RunAsync(
            new ProcessRequest(
                ffmpegPath,
                [
                    "-nostdin",
                    "-hide_banner",
                    "-loglevel", "error",
                    "-re",
                    "-i", inputPath,
                    "-f", "null",
                    "-",
                ],
                TimeSpan.FromSeconds(30)),
            cancellationToken: cancellation.Token);

        int? launchedProcess = null;
        for (int attempt = 0; attempt < 40 && launchedProcess is null; attempt++)
        {
            await Task.Delay(50);
            launchedProcess = Process.GetProcessesByName("ffmpeg")
                .Select(process => process.Id)
                .FirstOrDefault(processId => !before.Contains(processId));
            if (launchedProcess == 0)
            {
                launchedProcess = null;
            }
        }

        int launchedProcessId = launchedProcess
            ?? throw new InvalidOperationException(
                "Real ffmpeg process was not observed.");
        cancellation.Cancel();
        try
        {
            _ = await execution;
            throw new InvalidOperationException("Expected real ffmpeg cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        await Task.Delay(150);
        Assert(!IsProcessRunning(launchedProcessId),
            "Cancelled ffmpeg.exe was left running.");
    }

    private static void AssertWavArguments(
        IReadOnlyList<string> arguments,
        string inputPath)
    {
        string[] required =
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-progress", "pipe:1", "-nostats", "-n",
            "-i", inputPath, "-map", "0:a:0", "-vn",
            "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
            "-f", "wav",
        ];
        Assert(arguments.Take(required.Length).SequenceEqual(required),
            "Whisper WAV FFmpeg arguments changed.");
    }

    private static void AssertMonotonicFinished(IReadOnlyList<double> values)
    {
        Assert(values.Count > 0 && values[^1] == 1.0,
            "Media progress did not finish at 1.");
        Assert(values.All(value => double.IsFinite(value)
            && value is >= 0 and <= 1),
            "Media progress was not finite and bounded.");
        Assert(values.Zip(values.Skip(1), (left, right) => right > left).All(x => x),
            "Media progress was not strictly increasing.");
    }

    private static async Task AssertInspectionErrorAsync(
        MediaInspectionError expected,
        Func<Task<MediaMetadata>> operation)
    {
        try
        {
            _ = await operation();
            throw new InvalidOperationException(
                $"Expected media inspection error {expected}.");
        }
        catch (MediaInspectionException exception)
            when (exception.Error == expected)
        {
        }
    }

    private static async Task AssertConversionErrorAsync(
        MediaConversionError expected,
        Func<Task<string>> operation)
    {
        try
        {
            _ = await operation();
            throw new InvalidOperationException(
                $"Expected media conversion error {expected}.");
        }
        catch (MediaConversionException exception)
            when (exception.Error == expected)
        {
        }
    }

    private static async Task AssertCancelledMediaAsync(Task<string> operation)
    {
        try
        {
            _ = await operation;
            throw new InvalidOperationException("Expected media cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ProcessRequest FixtureRequest(
        ProcessInvocation invocation,
        IReadOnlyList<string> fixtureArguments,
        TimeSpan? timeout = null)
    {
        var arguments = new List<string>(invocation.ArgumentPrefix)
        {
            "--process-fixture",
        };
        arguments.AddRange(fixtureArguments);
        return new ProcessRequest(
            invocation.ExecutablePath,
            arguments,
            timeout ?? TimeSpan.FromSeconds(5));
    }

    private static ProcessInvocation CurrentProcessInvocation()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current executable is unavailable.");
        if (Path.GetFileNameWithoutExtension(executablePath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessInvocation(
                executablePath,
                [Assembly.GetExecutingAssembly().Location]);
        }

        return new ProcessInvocation(executablePath, []);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static MediaArguments ParseMediaArguments(string[] arguments)
    {
        if (arguments.Length != 7
            || arguments[1] != "--ffmpeg"
            || arguments[3] != "--ffprobe"
            || arguments[5] != "--input")
        {
            throw new ArgumentException(
                "Usage: --media-smoke --ffmpeg <path> --ffprobe <path> --input <path>");
        }

        return new MediaArguments(arguments[2], arguments[4], arguments[6]);
    }

    private sealed class DelegateProcessRunner : IProcessRunner
    {
        private readonly Func<
            ProcessRequest,
            Func<string, ValueTask>?,
            CancellationToken,
            Task<ProcessResult>> operation;

        internal DelegateProcessRunner(Func<
            ProcessRequest,
            Func<string, ValueTask>?,
            CancellationToken,
            Task<ProcessResult>> operation)
        {
            this.operation = operation;
        }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Func<string, ValueTask>? standardOutputHandler = null,
            CancellationToken cancellationToken = default) =>
            operation(request, standardOutputHandler, cancellationToken);
    }

    private sealed class MediaTestInputs : IDisposable
    {
        private MediaTestInputs(string root, string inputPath, string outputPath)
        {
            Root = root;
            InputPath = inputPath;
            OutputPath = outputPath;
        }

        internal string Root { get; }

        internal string InputPath { get; }

        internal string OutputPath { get; }

        internal static MediaTestInputs Create()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"pmt media Юникод с пробелами {Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string inputPath = Path.Combine(root, "вход & sample.wav");
            File.WriteAllText(inputPath, "fake media");
            return new MediaTestInputs(
                root,
                inputPath,
                Path.Combine(root, "Whisper результат.wav"));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record ProcessInvocation(
        string ExecutablePath,
        IReadOnlyList<string> ArgumentPrefix);

    private sealed record MediaArguments(
        string FFmpegPath,
        string FFprobePath,
        string InputPath);
}
