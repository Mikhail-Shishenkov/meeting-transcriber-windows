using PolinMegatranscriber.Core;

if (args.FirstOrDefault() == "--process-fixture")
{
    return await ProcessFixture.RunAsync(args.Skip(1).ToArray());
}

try
{
    await CoreSmoke.RunAsync(args);
    Console.WriteLine("Core smoke OK: all checks passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Core smoke FAILED: {exception}");
    return 1;
}

internal static partial class CoreSmoke
{
    internal static async Task RunAsync(string[] arguments)
    {
        await VerifyMappingProgressAndRussianLanguageAsync();
        await VerifyInputValidationAsync();
        await VerifyConcurrencyCancellationAndReuseAsync();
        await VerifyRuntimeErrorMappingAsync();
        await VerifyProgressFailureIsolationAsync();
        VerifyFFmpegProgressParser();
        await VerifyProcessRunnerContractAsync();
        await VerifyMediaServiceContractsAsync();
        await VerifyProcessingContractsAsync();

        if (arguments.Length > 0)
        {
            if (arguments[0] == "--media-smoke")
            {
                await RunRealMediaAsync(ParseMediaArguments(arguments));
            }
            else if (arguments[0] == "--processing-smoke")
            {
                await RunRealProcessingAsync(
                    ParseProcessingArguments(arguments));
            }
            else
            {
                await RunRealAsync(ParseRealArguments(arguments));
            }
        }
    }

    private static async Task VerifyMappingProgressAndRussianLanguageAsync()
    {
        using var inputs = TestInputs.Create();
        WhisperRuntimeSegment[] runtimeSegments =
        [
            new(120, 980, " Привет"),
            new(1_000, 2_340, " мир"),
        ];
        var runtime = new DelegateRuntime((request, report, _) =>
        {
            Assert(
                request.Language == TranscriptionLanguage.Russian,
                "Russian language was not forwarded.");
            Assert(
                request.Language.ToBridgeCode() == "ru",
                "Russian bridge code must be ru.");
            foreach (double value in new[]
                     {
                         -0.2,
                         0.4,
                         0.2,
                         double.NaN,
                         double.PositiveInfinity,
                     })
            {
                report(value);
            }

            return new WhisperRuntimeResult(runtimeSegments, "ru");
        });
        var progress = new RecordingProgress();
        var service = new WhisperTranscriptionService(runtime);

        TranscriptionResult result = await service.TranscribeAsync(
            new TranscriptionRequest(
                inputs.ModelPath,
                inputs.WavPath,
                TranscriptionLanguage.Russian),
            progress);

        runtimeSegments[0] = new WhisperRuntimeSegment(0, 0, "mutated");
        Assert(result.Segments.Count == 2, "Segments were not mapped.");
        Assert(result.Segments[0].Text == " Привет", "Result was not copied.");
        Assert(result.DetectedLanguage == "ru", "Detected language was not mapped.");
        Assert(
            progress.Values.SequenceEqual(new[] { 0.4, 1.0 }),
            "Progress was not finite, monotonic, and normalized.");
    }

    private static async Task VerifyInputValidationAsync()
    {
        using var inputs = TestInputs.Create();
        var runtime = new DelegateRuntime((_, _, _) =>
            new WhisperRuntimeResult([], null));
        var service = new WhisperTranscriptionService(runtime);

        await AssertErrorAsync(
            TranscriptionError.InvalidModel,
            () => service.TranscribeAsync(
                new TranscriptionRequest(
                    Path.Combine(inputs.Root, "missing-model.bin"),
                    inputs.WavPath)));
        await AssertErrorAsync(
            TranscriptionError.InvalidWav,
            () => service.TranscribeAsync(
                new TranscriptionRequest(
                    inputs.ModelPath,
                    Path.Combine(inputs.Root, "missing.wav"))));

        string emptyModel = Path.Combine(inputs.Root, "empty-model.bin");
        File.WriteAllBytes(emptyModel, []);
        await AssertErrorAsync(
            TranscriptionError.InvalidModel,
            () => service.TranscribeAsync(
                new TranscriptionRequest(emptyModel, inputs.WavPath)));
        await AssertErrorAsync(
            TranscriptionError.InvalidWav,
            () => service.TranscribeAsync(
                new TranscriptionRequest(inputs.ModelPath, inputs.Root)));

        using (File.Open(
                   inputs.WavPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            await AssertErrorAsync(
                TranscriptionError.InvalidWav,
                () => service.TranscribeAsync(
                    new TranscriptionRequest(
                        inputs.ModelPath,
                        inputs.WavPath)));
        }

        Assert(runtime.CallCount == 0, "Invalid inputs reached the runtime.");
    }

    private static async Task VerifyConcurrencyCancellationAndReuseAsync()
    {
        using var inputs = TestInputs.Create();
        var runtime = new BlockingThenSuccessRuntime();
        var service = new WhisperTranscriptionService(runtime);
        using var cancellation = new CancellationTokenSource();
        Task<TranscriptionResult> first = service.TranscribeAsync(
            new TranscriptionRequest(inputs.ModelPath, inputs.WavPath),
            cancellationToken: cancellation.Token);
        Assert(
            runtime.Started.Wait(TimeSpan.FromSeconds(5)),
            "Background runtime did not start.");

        await AssertErrorAsync(
            TranscriptionError.InferenceInProgress,
            () => service.TranscribeAsync(
                new TranscriptionRequest(inputs.ModelPath, inputs.WavPath)));
        cancellation.Cancel();
        await AssertCancelledAsync(first);
        Assert(runtime.CancellationObserved, "Cancellation token did not reach runtime.");

        TranscriptionResult retry = await service.TranscribeAsync(
            new TranscriptionRequest(inputs.ModelPath, inputs.WavPath));
        Assert(retry.Segments.Count == 0, "Service did not recover after cancellation.");
        Assert(runtime.CallCount == 2, "Unexpected runtime lifecycle count.");
    }

    private static async Task VerifyRuntimeErrorMappingAsync()
    {
        using var inputs = TestInputs.Create();
        (WhisperRuntimeError Runtime, TranscriptionError Domain)[] mappings =
        [
            (WhisperRuntimeError.RuntimeUnavailable,
                TranscriptionError.RuntimeUnavailable),
            (WhisperRuntimeError.InvalidModel, TranscriptionError.InvalidModel),
            (WhisperRuntimeError.InvalidWav, TranscriptionError.InvalidWav),
            (WhisperRuntimeError.UnsupportedWav,
                TranscriptionError.UnsupportedWav),
            (WhisperRuntimeError.InferenceFailed,
                TranscriptionError.InferenceFailed),
            (WhisperRuntimeError.InvalidResult,
                TranscriptionError.InvalidResult),
        ];
        foreach ((WhisperRuntimeError runtimeError, TranscriptionError domainError)
                 in mappings)
        {
            var runtime = new DelegateRuntime((_, _, _) =>
                throw new WhisperRuntimeException(runtimeError));
            var service = new WhisperTranscriptionService(runtime);
            TranscriptionException mapped = await AssertErrorAsync(
                domainError,
                () => service.TranscribeAsync(
                    new TranscriptionRequest(
                        inputs.ModelPath,
                        inputs.WavPath)));
            Assert(
                mapped.InnerException is null,
                "Runtime exception leaked through the public Core error.");
        }

        var invalidResultRuntime = new DelegateRuntime((_, _, _) =>
            new WhisperRuntimeResult(
                [new WhisperRuntimeSegment(10, 5, "invalid")],
                null));
        await AssertErrorAsync(
            TranscriptionError.InvalidResult,
            () => new WhisperTranscriptionService(invalidResultRuntime)
                .TranscribeAsync(
                    new TranscriptionRequest(
                        inputs.ModelPath,
                        inputs.WavPath)));
    }

    private static async Task VerifyProgressFailureIsolationAsync()
    {
        using var inputs = TestInputs.Create();
        var runtime = new DelegateRuntime((_, report, _) =>
        {
            report(0.5);
            return new WhisperRuntimeResult([], null);
        });
        var service = new WhisperTranscriptionService(runtime);

        TranscriptionResult result = await service.TranscribeAsync(
            new TranscriptionRequest(inputs.ModelPath, inputs.WavPath),
            new ThrowingProgress());
        Assert(result.Segments.Count == 0, "Progress callback broke inference.");
    }

    private static async Task RunRealAsync(RealArguments arguments)
    {
        var progress = new RecordingProgress();
        var service = new WhisperTranscriptionService();
        TranscriptionResult result = await service.TranscribeAsync(
            new TranscriptionRequest(arguments.ModelPath, arguments.WavPath),
            progress);
        Assert(result.Segments.Count > 0, "Real Core inference returned no segments.");
        Assert(
            result.Segments.All(segment =>
                segment.StartMilliseconds >= 0
                && segment.EndMilliseconds >= segment.StartMilliseconds
                && !string.IsNullOrWhiteSpace(segment.Text)),
            "Real Core inference returned an invalid segment.");
        Assert(
            progress.Values.Count > 0 && progress.Values[^1] == 1.0,
            "Real Core progress did not finish at 1.");

        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));
        await AssertCancelledAsync(
            new WhisperTranscriptionService().TranscribeAsync(
                new TranscriptionRequest(
                    arguments.ModelPath,
                    arguments.WavPath),
                cancellationToken: cancellation.Token));
        Console.WriteLine(
            $"Core real smoke OK: {result.Segments.Count} segment(s), "
            + $"language={result.DetectedLanguage ?? "unknown"}, cancellation observed.");
        foreach (TranscriptionSegment segment in result.Segments)
        {
            Console.WriteLine(
                $"[{segment.StartMilliseconds}..{segment.EndMilliseconds} ms]"
                + segment.Text);
        }
    }

    private static RealArguments ParseRealArguments(string[] arguments)
    {
        if (arguments.Length != 4
            || arguments[0] != "--model"
            || arguments[2] != "--wav")
        {
            throw new ArgumentException(
                "Usage: [--model <path> --wav <path>]");
        }

        return new RealArguments(arguments[1], arguments[3]);
    }

    private static async Task<TranscriptionException> AssertErrorAsync(
        TranscriptionError expected,
        Func<Task<TranscriptionResult>> operation)
    {
        try
        {
            _ = await operation();
            throw new InvalidOperationException(
                $"Expected transcription error {expected}.");
        }
        catch (TranscriptionException exception)
            when (exception.Error == expected)
        {
            return exception;
        }

        throw new InvalidOperationException("Unreachable error assertion state.");
    }

    private static async Task AssertCancelledAsync(
        Task<TranscriptionResult> operation)
    {
        try
        {
            _ = await operation;
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class DelegateRuntime : IWhisperRuntime
    {
        private readonly Func<
            TranscriptionRequest,
            Action<double>,
            CancellationToken,
            WhisperRuntimeResult> operation;

        internal DelegateRuntime(Func<
            TranscriptionRequest,
            Action<double>,
            CancellationToken,
            WhisperRuntimeResult> operation)
        {
            this.operation = operation;
        }

        internal int CallCount { get; private set; }

        public WhisperRuntimeResult Transcribe(
            TranscriptionRequest request,
            Action<double> progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return operation(request, progress, cancellationToken);
        }
    }

    private sealed class BlockingThenSuccessRuntime : IWhisperRuntime
    {
        internal ManualResetEventSlim Started { get; } = new();

        internal int CallCount { get; private set; }

        internal bool CancellationObserved { get; private set; }

        public WhisperRuntimeResult Transcribe(
            TranscriptionRequest request,
            Action<double> progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                Started.Set();
                try
                {
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }

            return new WhisperRuntimeResult([], null);
        }
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        internal List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private sealed class ThrowingProgress : IProgress<double>
    {
        public void Report(double value) =>
            throw new InvalidOperationException("Test progress failure.");
    }

    private sealed class TestInputs : IDisposable
    {
        private TestInputs(string root, string modelPath, string wavPath)
        {
            Root = root;
            ModelPath = modelPath;
            WavPath = wavPath;
        }

        internal string Root { get; }

        internal string ModelPath { get; }

        internal string WavPath { get; }

        internal static TestInputs Create()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"pmt-core-smoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string modelPath = Path.Combine(root, "model.bin");
            string wavPath = Path.Combine(root, "audio.wav");
            File.WriteAllText(modelPath, "model");
            File.WriteAllText(wavPath, "wav");
            return new TestInputs(root, modelPath, wavPath);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record RealArguments(string ModelPath, string WavPath);
}
