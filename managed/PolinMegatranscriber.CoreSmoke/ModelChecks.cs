using System.Net;
using System.Security.Cryptography;
using System.Text;
using PolinMegatranscriber.Core;

internal static partial class CoreSmoke
{
    private static async Task VerifyModelContractsAsync()
    {
        VerifyEmbeddedModelManifest();
        using var inputs = ModelTestInputs.Create();
        byte[] smallContent = Encoding.UTF8.GetBytes(
            "small deterministic model fixture");
        ModelManifest manifest = CreateTestManifest(smallContent);
        var downloader = new ByteArrayModelDownloader(smallContent);
        var manager = new ModelManager(
            manifest,
            new ModelStorage(inputs.StorageRoot),
            new StreamingModelFileVerifier(),
            downloader);

        Assert(await manager.GetStatusAsync(WhisperModel.Small)
            == ModelInstallationStatus.Absent,
            "Missing model was not Absent.");
        var progress = new RecordingModelProgress();
        string installed = await manager.DownloadAndInstallAsync(
            WhisperModel.Small,
            progress);
        Assert(File.Exists(installed), "Model was not installed.");
        Assert(await manager.GetStatusAsync(WhisperModel.Small)
            == ModelInstallationStatus.Verified,
            "Installed model was not Verified.");
        Assert(await manager.GetVerifiedPathAsync(WhisperModel.Small)
            == installed,
            "Verified model path changed.");
        AssertModelProgress(progress.Values, smallContent.LongLength);

        _ = await manager.DownloadAndInstallAsync(WhisperModel.Small);
        Assert(downloader.CallCount == 1,
            "Verified model was downloaded again.");

        await File.AppendAllTextAsync(installed, "corruption");
        Assert(await manager.GetStatusAsync(WhisperModel.Small)
            == ModelInstallationStatus.Corrupted,
            "Changed model was not Corrupted.");
        Assert(await manager.GetVerifiedPathAsync(WhisperModel.Small) is null,
            "Corrupted model leaked through verified path.");
        await manager.DeleteAsync(WhisperModel.Small);
        Assert(await manager.GetStatusAsync(WhisperModel.Small)
            == ModelInstallationStatus.Absent,
            "Deleted model was not Absent.");

        await VerifyModelCancellationCleanupAsync(inputs, manifest);
        await VerifyHttpDownloaderAsync(inputs);
    }

    private static void VerifyEmbeddedModelManifest()
    {
        ModelManifest manifest = ModelManifest.LoadEmbedded();
        Assert(manifest.Models.Count == 2,
            "Embedded manifest must contain exactly two models.");
        ModelDescriptor small = manifest.Get(WhisperModel.Small);
        ModelDescriptor medium = manifest.Get(WhisperModel.Medium);
        Assert(small.Filename == "ggml-small.bin"
            && small.SizeBytes == 487_601_967
            && small.Sha256
                == "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"
            && small.DownloadUri.AbsoluteUri.Contains(
                "c521a4b02f422512d734391fdf08bb08c0862f68",
                StringComparison.Ordinal),
            "Small manifest entry changed.");
        Assert(medium.Filename == "ggml-medium.bin"
            && medium.SizeBytes == 1_533_763_059
            && medium.Sha256
                == "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"
            && medium.DownloadUri.Scheme == Uri.UriSchemeHttps,
            "Medium manifest entry changed.");
        Assert(manifest.Models[0].DisplayName == "Быстрее"
            && manifest.Models[1].DisplayName == "Точнее",
            "Product model names changed.");

        const string unsafeManifest = """
            {
              "schemaVersion": 1,
              "source": {
                "repository": "repo",
                "revision": "revision",
                "license": "MIT"
              },
              "models": [
                {
                  "id": "small",
                  "filename": "../ggml-small.bin",
                  "url": "https://example.invalid/small",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "sizeBytes": 1,
                  "displaySize": "1"
                },
                {
                  "id": "medium",
                  "filename": "ggml-medium.bin",
                  "url": "http://example.invalid/medium",
                  "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "sizeBytes": 1,
                  "displaySize": "1"
                }
              ]
            }
            """;
        try
        {
            using var stream = new MemoryStream(
                Encoding.UTF8.GetBytes(unsafeManifest));
            _ = ModelManifest.Load(stream);
            throw new InvalidOperationException(
                "Unsafe model manifest was accepted.");
        }
        catch (ModelManagerException exception)
            when (exception.Error
                == ModelManagementError.ManifestUnavailable)
        {
        }
    }

    private static async Task VerifyModelCancellationCleanupAsync(
        ModelTestInputs inputs,
        ModelManifest manifest)
    {
        var downloader = new BlockingModelDownloader();
        var manager = new ModelManager(
            manifest,
            new ModelStorage(inputs.CancellationStorageRoot),
            new StreamingModelFileVerifier(),
            downloader);
        using var cancellation = new CancellationTokenSource();
        Task<string> operation = manager.DownloadAndInstallAsync(
            WhisperModel.Small,
            cancellationToken: cancellation.Token);
        await downloader.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            _ = await operation;
            throw new InvalidOperationException(
                "Expected model download cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert(!Directory.EnumerateFiles(
                inputs.CancellationStorageRoot,
                "*.partial",
                SearchOption.AllDirectories)
            .Any(),
            "Cancelled model download left a partial file.");
    }

    private static async Task VerifyHttpDownloaderAsync(ModelTestInputs inputs)
    {
        byte[] content = Encoding.UTF8.GetBytes("streamed https response");
        using var client = new HttpClient(
            new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://cdn.example.invalid/model.bin"),
                Content = new ByteArrayContent(content),
            }));
        var downloader = new HttpModelDownloader(client);
        string destination = Path.Combine(inputs.Root, "http-model.partial");
        var received = new List<long>();
        await downloader.DownloadAsync(
            new Uri("https://example.invalid/model.bin"),
            destination,
            content.LongLength,
            () => { },
            received.Add);
        Assert(File.ReadAllBytes(destination).SequenceEqual(content)
            && received.Count > 0
            && received[^1] == content.LongLength,
            "HttpClient downloader did not stream the response.");
        File.Delete(destination);

        using var redirectClient = new HttpClient(
            new StaticHttpHandler(_ => new HttpResponseMessage(
                HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://unsafe.invalid/model") },
            }));
        try
        {
            await new HttpModelDownloader(redirectClient).DownloadAsync(
                new Uri("https://example.invalid/model"),
                destination,
                content.LongLength,
                () => { },
                _ => { });
            throw new InvalidOperationException(
                "HTTPS downgrade redirect was accepted.");
        }
        catch (ModelDownloadException exception)
            when (exception.Error == ModelDownloadError.InsecureRedirect)
        {
        }
    }

    private static async Task RunRealModelAsync(ModelArguments arguments)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"TASK 008 модели Юникод и пробелы {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string storageRoot = Path.Combine(root, "Хранилище моделей");
            var storage = new ModelStorage(storageRoot);
            storage.EnsureDirectories();
            ModelManifest manifest = ModelManifest.LoadEmbedded();
            var manager = new ModelManager(
                manifest,
                storage,
                new StreamingModelFileVerifier(),
                new FailingModelDownloader());
            string installedPath = storage.ModelPath(
                manifest.Get(WhisperModel.Small));
            File.Copy(arguments.SourceModelPath, installedPath, overwrite: false);
            Assert(await manager.GetStatusAsync(WhisperModel.Small)
                == ModelInstallationStatus.Verified,
                "Real Small copy was not Verified.");
            string verifiedPath = await manager.GetVerifiedPathAsync(
                    WhisperModel.Small)
                ?? throw new InvalidOperationException(
                    "Real verified Small path is missing.");
            Assert(verifiedPath.Contains("Хранилище моделей"),
                "Verified model did not use Unicode storage path.");

            using (var file = new FileStream(
                       installedPath,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.None))
            {
                file.SetLength(file.Length - 1);
            }
            Assert(await manager.GetStatusAsync(WhisperModel.Small)
                == ModelInstallationStatus.Corrupted,
                "Truncated real Small was not Corrupted.");
            await manager.DeleteAsync(WhisperModel.Small);
            Assert(await manager.GetStatusAsync(WhisperModel.Small)
                == ModelInstallationStatus.Absent,
                "Deleted real Small was not Absent.");

            File.Copy(arguments.SourceModelPath, installedPath, overwrite: false);
            verifiedPath = await manager.GetVerifiedPathAsync(
                    WhisperModel.Small)
                ?? throw new InvalidOperationException(
                    "Restored real Small was not verified.");
            string inputPath = Path.Combine(root, "JFK вход с кириллицей.wav");
            File.Copy(arguments.InputPath, inputPath, overwrite: false);
            string resultsDirectory = Path.Combine(root, "Только текст результат");
            Directory.CreateDirectory(resultsDirectory);
            string workspaceRoot = Path.Combine(root, ".processing-workspaces");
            var locator = new FixedMediaToolLocator(
                arguments.FFmpegPath,
                arguments.FFprobePath);
            var processing = new ProcessingService(
                new FFprobeMediaInspector(locator),
                new FFmpegMediaPipeline(locator),
                new WhisperTranscriptionService(),
                new TranscriptExporter(),
                new JobWorkspaceManager(workspaceRoot));
            ProcessingResult result = await processing.ProcessAsync(
                new ProcessingRequest(
                    inputPath,
                    ProcessingMode.TextOnly,
                    resultsDirectory,
                    verifiedPath,
                    TranscriptionLanguage.Automatic));
            AssertExtensions(result.OutputFiles, [".txt", ".srt"]);
            ValidateRealTranscript(result);
            AssertWorkspaceEmpty(workspaceRoot);
            Assert(!Directory.EnumerateFiles(
                    storageRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Any(path => path.EndsWith(
                    ".partial",
                    StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(
                        ".installing",
                        StringComparison.OrdinalIgnoreCase)),
                "Real model smoke left temporary model files.");
            Console.WriteLine(
                "Model real smoke OK: Small Verified -> Corrupted -> Absent -> "
                + "Verified; Unicode storage and TextOnly TXT/SRT passed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ModelManifest CreateTestManifest(byte[] smallContent)
    {
        string smallHash = Convert.ToHexString(
                SHA256.HashData(smallContent))
            .ToLowerInvariant();
        byte[] mediumContent = Encoding.UTF8.GetBytes("medium fixture");
        string mediumHash = Convert.ToHexString(
                SHA256.HashData(mediumContent))
            .ToLowerInvariant();
        string json = $$"""
            {
              "schemaVersion": 1,
              "source": {
                "repository": "test",
                "revision": "test",
                "license": "MIT"
              },
              "models": [
                {
                  "id": "small",
                  "filename": "ggml-small.bin",
                  "url": "https://example.invalid/small",
                  "sha256": "{{smallHash}}",
                  "sizeBytes": {{smallContent.LongLength}},
                  "displaySize": "test"
                },
                {
                  "id": "medium",
                  "filename": "ggml-medium.bin",
                  "url": "https://example.invalid/medium",
                  "sha256": "{{mediumHash}}",
                  "sizeBytes": {{mediumContent.LongLength}},
                  "displaySize": "test"
                }
              ]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return ModelManifest.Load(stream);
    }

    private static void AssertModelProgress(
        IReadOnlyList<ModelDownloadProgress> values,
        long expectedBytes)
    {
        Assert(values.Count > 0
            && values[^1] == new ModelDownloadProgress(
                expectedBytes,
                expectedBytes,
                1.0),
            "Model progress did not finish at 1.");
        Assert(values.All(value =>
                value.DownloadedBytes is >= 0
                && value.DownloadedBytes <= value.ExpectedBytes
                && value.ExpectedBytes == expectedBytes
                && double.IsFinite(value.Fraction)
                && value.Fraction is >= 0 and <= 1),
            "Model progress is invalid.");
        Assert(values.Zip(values.Skip(1),
                (left, right) =>
                    right.DownloadedBytes >= left.DownloadedBytes
                    && right.Fraction >= left.Fraction)
            .All(value => value),
            "Model progress regressed.");
    }

    private static ModelArguments ParseModelArguments(string[] arguments)
    {
        if (arguments.Length != 9
            || arguments[1] != "--ffmpeg"
            || arguments[3] != "--ffprobe"
            || arguments[5] != "--input"
            || arguments[7] != "--source-model")
        {
            throw new ArgumentException(
                "Usage: --model-smoke --ffmpeg <path> --ffprobe <path> "
                + "--input <path> --source-model <path>");
        }

        return new ModelArguments(
            arguments[2],
            arguments[4],
            arguments[6],
            arguments[8]);
    }

    private sealed class ByteArrayModelDownloader : IModelDownloader
    {
        private readonly byte[] content;

        internal ByteArrayModelDownloader(byte[] content)
        {
            this.content = content;
        }

        internal int CallCount { get; private set; }

        public async Task DownloadAsync(
            Uri source,
            string destinationPath,
            long expectedBytes,
            Action destinationCreated,
            Action<long> bytesReceived,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            await using var stream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4_096,
                FileOptions.Asynchronous);
            destinationCreated();
            await stream.WriteAsync(content, cancellationToken);
            bytesReceived(content.LongLength);
        }
    }

    private sealed class BlockingModelDownloader : IModelDownloader
    {
        internal TaskCompletionSource Created { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DownloadAsync(
            Uri source,
            string destinationPath,
            long expectedBytes,
            Action destinationCreated,
            Action<long> bytesReceived,
            CancellationToken cancellationToken = default)
        {
            await using var stream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4_096,
                FileOptions.Asynchronous);
            destinationCreated();
            await stream.WriteAsync(
                new byte[] { 1, 2, 3 },
                cancellationToken);
            Created.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingModelDownloader : IModelDownloader
    {
        public Task DownloadAsync(
            Uri source,
            string destinationPath,
            long expectedBytes,
            Action destinationCreated,
            Action<long> bytesReceived,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Real model smoke must not download a model.");
    }

    private sealed class RecordingModelProgress
        : IProgress<ModelDownloadProgress>
    {
        internal List<ModelDownloadProgress> Values { get; } = [];

        public void Report(ModelDownloadProgress value) => Values.Add(value);
    }

    private sealed class StaticHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> response;

        internal StaticHttpHandler(
            Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            this.response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class ModelTestInputs : IDisposable
    {
        private ModelTestInputs(string root)
        {
            Root = root;
            StorageRoot = Path.Combine(root, "модели с пробелами");
            CancellationStorageRoot = Path.Combine(root, "отмена загрузки");
        }

        internal string Root { get; }

        internal string StorageRoot { get; }

        internal string CancellationStorageRoot { get; }

        internal static ModelTestInputs Create()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"model checks Юникод {Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new ModelTestInputs(root);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record ModelArguments(
        string FFmpegPath,
        string FFprobePath,
        string InputPath,
        string SourceModelPath);
}
