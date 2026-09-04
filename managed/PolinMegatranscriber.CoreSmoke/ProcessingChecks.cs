using System.Text;
using System.Text.RegularExpressions;
using PolinMegatranscriber.Core;

internal static partial class CoreSmoke
{
    private static async Task VerifyProcessingContractsAsync()
    {
        VerifyTranscriptFormatting();
        using var inputs = ProcessingTestInputs.Create();
        var inspector = new FakeMediaInspector();
        var converter = new FakeMediaConverter();
        var transcriber = new FakeTranscriber();
        var workspaceManager = new JobWorkspaceManager(inputs.WorkspaceRoot);
        var service = new ProcessingService(
            inspector,
            converter,
            transcriber,
            new TranscriptExporter(),
            workspaceManager);

        string audioResults = inputs.CreateResultsDirectory("Только аудио");
        var audioProgress = new RecordingProcessingProgress();
        ProcessingResult audio = await service.ProcessAsync(
            new ProcessingRequest(
                inputs.InputPath,
                ProcessingMode.AudioOnly,
                audioResults,
                ModelPath: Path.Combine(inputs.Root, "missing-model.bin")),
            audioProgress);
        Assert(audio.OutputFiles.Count == 1
            && Path.GetExtension(audio.OutputFiles[0]) == ".mp3",
            "AudioOnly did not publish exactly one MP3.");
        Assert(audio.Transcription is null,
            "AudioOnly returned a transcription.");
        Assert(transcriber.CallCount == 0,
            "AudioOnly invoked Whisper or required its model.");
        Assert(!audioProgress.Values.Any(value =>
                value.Phase is ProcessingPhase.Transcription
                    or ProcessingPhase.Exporting),
            "AudioOnly published a fictitious text phase.");
        AssertProcessingProgress(audioProgress.Values);

        string callbackResults = inputs.CreateResultsDirectory(
            "Исключение progress");
        ProcessingResult callbackSafe = await service.ProcessAsync(
            new ProcessingRequest(
                inputs.InputPath,
                ProcessingMode.AudioOnly,
                callbackResults),
            new ThrowingProcessingProgress());
        Assert(callbackSafe.OutputFiles.All(File.Exists),
            "Processing progress callback broke the job.");

        string combinedResults = inputs.CreateResultsDirectory("Всё вместе");
        string expectedFinalMp3 = Path.Combine(
            combinedResults,
            "вход с пробелами.mp3");
        transcriber.BeforeReturn = () => Assert(
            !File.Exists(expectedFinalMp3),
            "Combined MP3 was published before transcription/export.");
        var combinedProgress = new RecordingProcessingProgress();
        ProcessingResult combined = await service.ProcessAsync(
            new ProcessingRequest(
                inputs.InputPath,
                ProcessingMode.AudioAndText,
                combinedResults,
                inputs.ModelPath),
            combinedProgress);
        Assert(combined.OutputFiles.Count == 3,
            "AudioAndText did not publish three outputs.");
        Assert(combined.OutputFiles.All(File.Exists),
            "AudioAndText returned an unpublished output.");
        Assert(combined.Transcription is not null,
            "AudioAndText lost its transcription result.");
        Assert(transcriber.LastRequest?.Language == TranscriptionLanguage.Russian,
            "Processing default language is not Russian.");
        AssertProcessingProgress(combinedProgress.Values);
        AssertWorkspaceEmpty(inputs.WorkspaceRoot);

        string conflictResults = inputs.CreateResultsDirectory("Конфликт");
        string conflictPath = Path.Combine(
            conflictResults,
            "вход с пробелами.mp3");
        byte[] userBytes = Encoding.UTF8.GetBytes("USER FILE");
        File.WriteAllBytes(conflictPath, userBytes);
        await AssertProcessingErrorAsync(
            ProcessingError.OutputConflict,
            () => service.ProcessAsync(new ProcessingRequest(
                inputs.InputPath,
                ProcessingMode.AudioOnly,
                conflictResults)));
        Assert(File.ReadAllBytes(conflictPath).SequenceEqual(userBytes),
            "Output conflict changed the user's file.");

        await VerifyProcessingConcurrencyAndReuseAsync(inputs);
        AssertWorkspaceEmpty(inputs.WorkspaceRoot);
    }

    private static void VerifyTranscriptFormatting()
    {
        var result = new TranscriptionResult(
            [
                new TranscriptionSegment(0, 1_234, " Первый"),
                new TranscriptionSegment(3_661_001, 3_662_045, "второй"),
            ],
            "ru");
        Assert(
            TranscriptExporter.FormatTxt(result.Segments)
                == " Первый\nвторой",
            "TXT formatting changed segment text.");
        const string expectedSrt =
            "1\n00:00:00,000 --> 00:00:01,234\n Первый\n\n"
            + "2\n01:01:01,001 --> 01:01:02,045\nвторой\n";
        Assert(TranscriptExporter.FormatSrt(result.Segments) == expectedSrt,
            "SRT formatting is invalid.");

        try
        {
            _ = TranscriptExporter.FormatSrt(
                [
                    new TranscriptionSegment(10, 20, "later"),
                    new TranscriptionSegment(5, 6, "backwards"),
                ]);
            throw new InvalidOperationException(
                "Expected chronological segment validation.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static async Task VerifyProcessingConcurrencyAndReuseAsync(
        ProcessingTestInputs inputs)
    {
        var inspector = new BlockingOnceMediaInspector();
        var service = new ProcessingService(
            inspector,
            new FakeMediaConverter(),
            new FakeTranscriber(),
            new TranscriptExporter(),
            new JobWorkspaceManager(inputs.WorkspaceRoot));
        string results = inputs.CreateResultsDirectory("Повтор после отмены");
        var request = new ProcessingRequest(
            inputs.InputPath,
            ProcessingMode.AudioOnly,
            results);
        using var cancellation = new CancellationTokenSource();
        Task<ProcessingResult> first = service.ProcessAsync(
            request,
            cancellationToken: cancellation.Token);
        await inspector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertProcessingErrorAsync(
            ProcessingError.ProcessingInProgress,
            () => service.ProcessAsync(request));
        cancellation.Cancel();
        await AssertProcessingCancelledAsync(first);

        ProcessingResult retry = await service.ProcessAsync(request);
        Assert(retry.OutputFiles.Count == 1
            && File.Exists(retry.OutputFiles[0]),
            "Processing service did not recover after cancellation.");
    }

    private static async Task RunRealProcessingAsync(
        ProcessingArguments arguments)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"TASK 007 полный цикл Юникод {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string inputPath = Path.Combine(
                root,
                "JFK пример с кириллицей и пробелами.wav");
            File.Copy(arguments.InputPath, inputPath, overwrite: false);
            string workspaceRoot = Path.Combine(root, ".workspaces");
            var locator = new FixedMediaToolLocator(
                arguments.FFmpegPath,
                arguments.FFprobePath);
            var inspector = new FFprobeMediaInspector(locator);
            var service = new ProcessingService(
                inspector,
                new FFmpegMediaPipeline(locator),
                new WhisperTranscriptionService(),
                new TranscriptExporter(),
                new JobWorkspaceManager(workspaceRoot));

            ProcessingResult audio = await RunRealModeAsync(
                service,
                inputPath,
                arguments.ModelPath,
                ProcessingMode.AudioOnly,
                Path.Combine(root, "Результаты — только аудио"));
            AssertExtensions(audio.OutputFiles, [".mp3"]);
            Assert(audio.Transcription is null,
                "Real AudioOnly returned transcription.");

            ProcessingResult text = await RunRealModeAsync(
                service,
                inputPath,
                arguments.ModelPath,
                ProcessingMode.TextOnly,
                Path.Combine(root, "Результаты — только текст"));
            AssertExtensions(text.OutputFiles, [".txt", ".srt"]);
            ValidateRealTranscript(text);

            ProcessingResult combined = await RunRealModeAsync(
                service,
                inputPath,
                arguments.ModelPath,
                ProcessingMode.AudioAndText,
                Path.Combine(root, "Результаты — аудио и текст"));
            AssertExtensions(combined.OutputFiles, [".mp3", ".txt", ".srt"]);
            ValidateRealTranscript(combined);

            string conflictDirectory = Path.Combine(root, "Проверка конфликта");
            Directory.CreateDirectory(conflictDirectory);
            string conflictPath = Path.Combine(
                conflictDirectory,
                "JFK пример с кириллицей и пробелами.mp3");
            byte[] original = Encoding.UTF8.GetBytes("USER OWNED CONTENT");
            File.WriteAllBytes(conflictPath, original);
            await AssertProcessingErrorAsync(
                ProcessingError.OutputConflict,
                () => service.ProcessAsync(new ProcessingRequest(
                    inputPath,
                    ProcessingMode.AudioOnly,
                    conflictDirectory)));
            Assert(File.ReadAllBytes(conflictPath).SequenceEqual(original),
                "Real output conflict modified the user file.");

            AssertWorkspaceEmpty(workspaceRoot);
            string[] garbage = Directory.EnumerateFileSystemEntries(
                    root,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path =>
                    path.EndsWith(
                        ".partial",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert(garbage.Length == 0,
                "Real processing left partial/workspace artifacts.");
            Console.WriteLine(
                "Processing real smoke OK: AudioOnly=MP3, "
                + "TextOnly=TXT+SRT, AudioAndText=MP3+TXT+SRT; "
                + "Unicode paths, Whisper, conflict and cleanup passed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ProcessingResult> RunRealModeAsync(
        ProcessingService service,
        string inputPath,
        string modelPath,
        ProcessingMode mode,
        string resultsDirectory)
    {
        Directory.CreateDirectory(resultsDirectory);
        var progress = new RecordingProcessingProgress();
        ProcessingResult result = await service.ProcessAsync(
            new ProcessingRequest(
                inputPath,
                mode,
                resultsDirectory,
                ModelPath: mode == ProcessingMode.AudioOnly
                    ? null
                    : modelPath,
                Language: TranscriptionLanguage.Automatic),
            progress);
        AssertProcessingProgress(progress.Values);
        Assert(result.OutputFiles.All(path =>
                File.Exists(path) && new FileInfo(path).Length > 0),
            $"Real {mode} produced an empty output.");
        Assert(Directory.EnumerateFiles(resultsDirectory).Count()
            == result.OutputFiles.Count,
            $"Real {mode} published unexpected files.");
        return result;
    }

    private static void ValidateRealTranscript(ProcessingResult result)
    {
        Assert(result.Transcription is not null
            && result.Transcription.Segments.Count > 0,
            "Real text mode returned no transcription.");
        string txtPath = result.OutputFiles.Single(path =>
            Path.GetExtension(path) == ".txt");
        string srtPath = result.OutputFiles.Single(path =>
            Path.GetExtension(path) == ".srt");
        var strictUtf8 = new UTF8Encoding(false, true);
        string txt = strictUtf8.GetString(File.ReadAllBytes(txtPath));
        string srt = strictUtf8.GetString(File.ReadAllBytes(srtPath));
        Assert(txt.Contains("fellow Americans", StringComparison.OrdinalIgnoreCase),
            "TXT does not contain the expected JFK phrase.");
        Assert(Regex.IsMatch(
                srt,
                @"\A1\n\d{2}:\d{2}:\d{2},\d{3} --> \d{2}:\d{2}:\d{2},\d{3}\n",
                RegexOptions.CultureInvariant),
            "SRT does not start with a valid timestamp block.");
    }

    private static void AssertExtensions(
        IReadOnlyList<string> paths,
        IReadOnlyList<string> extensions)
    {
        string[] actual = paths
            .Select(path => Path.GetExtension(path) ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = extensions
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert(actual.SequenceEqual(expected),
            "Processing result has unexpected file types.");
    }

    private static void AssertProcessingProgress(
        IReadOnlyList<ProcessingProgress> values)
    {
        Assert(values.Count > 0
            && values[^1] == new ProcessingProgress(
                ProcessingPhase.Completed,
                1.0),
            "Processing progress did not complete at 1.");
        Assert(values.All(value => double.IsFinite(value.Fraction)
            && value.Fraction is >= 0 and <= 1),
            "Processing progress is not finite and bounded.");
        Assert(values.Zip(values.Skip(1),
                (left, right) => right.Fraction >= left.Fraction)
            .All(value => value),
            "Processing progress regressed.");
    }

    private static void AssertWorkspaceEmpty(string workspaceRoot)
    {
        if (Directory.Exists(workspaceRoot))
        {
            Assert(!Directory.EnumerateFileSystemEntries(workspaceRoot).Any(),
                "Job workspace was not cleaned.");
        }
    }

    private static async Task AssertProcessingErrorAsync(
        ProcessingError expected,
        Func<Task<ProcessingResult>> operation)
    {
        try
        {
            _ = await operation();
            throw new InvalidOperationException(
                $"Expected processing error {expected}.");
        }
        catch (ProcessingException exception)
            when (exception.Error == expected)
        {
        }
    }

    private static async Task AssertProcessingCancelledAsync(
        Task<ProcessingResult> operation)
    {
        try
        {
            _ = await operation;
            throw new InvalidOperationException(
                "Expected processing cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ProcessingArguments ParseProcessingArguments(
        string[] arguments)
    {
        if (arguments.Length != 9
            || arguments[1] != "--ffmpeg"
            || arguments[3] != "--ffprobe"
            || arguments[5] != "--input"
            || arguments[7] != "--model")
        {
            throw new ArgumentException(
                "Usage: --processing-smoke --ffmpeg <path> --ffprobe <path> "
                + "--input <path> --model <path>");
        }

        return new ProcessingArguments(
            arguments[2],
            arguments[4],
            arguments[6],
            arguments[8]);
    }

    private sealed class FakeMediaInspector : IMediaInspector
    {
        public Task<MediaMetadata> InspectAsync(
            string inputPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MediaMetadata(
                [new AudioStreamMetadata(0, "pcm_s16le", 16_000, 1, TimeSpan.FromSeconds(2))],
                TimeSpan.FromSeconds(2)));
        }
    }

    private sealed class BlockingOnceMediaInspector : IMediaInspector
    {
        private int callCount;

        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MediaMetadata> InspectAsync(
            string inputPath,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new MediaMetadata(
                [new AudioStreamMetadata(0, "pcm_s16le", 16_000, 1, TimeSpan.FromSeconds(2))],
                TimeSpan.FromSeconds(2));
        }
    }

    private sealed class FakeMediaConverter : IMediaConversionService
    {
        public Task<string> ConvertToMp3Async(
            string inputPath,
            string outputPath,
            TimeSpan? duration = null,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            WriteAsync(outputPath, "mp3", progress, cancellationToken);

        public Task<string> ConvertToWhisperWavAsync(
            string inputPath,
            string outputPath,
            TimeSpan? duration = null,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            WriteAsync(outputPath, "wav", progress, cancellationToken);

        private static async Task<string> WriteAsync(
            string outputPath,
            string content,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(
                outputPath,
                content,
                Encoding.UTF8,
                cancellationToken);
            progress?.Report(0.5);
            progress?.Report(1.0);
            return outputPath;
        }
    }

    private sealed class FakeTranscriber : IWhisperTranscriptionService
    {
        internal int CallCount { get; private set; }

        internal TranscriptionRequest? LastRequest { get; private set; }

        internal Action? BeforeReturn { get; set; }

        public Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            progress?.Report(0.5);
            BeforeReturn?.Invoke();
            return Task.FromResult(new TranscriptionResult(
                [new TranscriptionSegment(0, 1_000, "Тестовый текст")],
                "ru"));
        }
    }

    private sealed class RecordingProcessingProgress
        : IProgress<ProcessingProgress>
    {
        internal List<ProcessingProgress> Values { get; } = [];

        public void Report(ProcessingProgress value) => Values.Add(value);
    }

    private sealed class ThrowingProcessingProgress
        : IProgress<ProcessingProgress>
    {
        public void Report(ProcessingProgress value) =>
            throw new InvalidOperationException(
                "Test processing progress failure.");
    }

    private sealed class ProcessingTestInputs : IDisposable
    {
        private ProcessingTestInputs(
            string root,
            string inputPath,
            string modelPath,
            string workspaceRoot)
        {
            Root = root;
            InputPath = inputPath;
            ModelPath = modelPath;
            WorkspaceRoot = workspaceRoot;
        }

        internal string Root { get; }

        internal string InputPath { get; }

        internal string ModelPath { get; }

        internal string WorkspaceRoot { get; }

        internal static ProcessingTestInputs Create()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"processing checks Юникод {Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string inputPath = Path.Combine(root, "вход с пробелами.wav");
            string modelPath = Path.Combine(root, "модель.bin");
            File.WriteAllText(inputPath, "input");
            File.WriteAllText(modelPath, "model");
            return new ProcessingTestInputs(
                root,
                inputPath,
                modelPath,
                Path.Combine(root, ".workspaces"));
        }

        internal string CreateResultsDirectory(string name)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record ProcessingArguments(
        string FFmpegPath,
        string FFprobePath,
        string InputPath,
        string ModelPath);
}
