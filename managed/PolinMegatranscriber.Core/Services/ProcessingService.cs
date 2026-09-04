using System.Runtime.ExceptionServices;

namespace PolinMegatranscriber.Core;

public interface IProcessingService
{
    Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessingService : IProcessingService
{
    private readonly IMediaInspector mediaInspector;
    private readonly IMediaConversionService mediaConverter;
    private readonly IWhisperTranscriptionService transcriber;
    private readonly ITranscriptExporter transcriptExporter;
    private readonly IJobWorkspaceManager workspaceManager;
    private int isRunning;

    public ProcessingService(IMediaToolLocator mediaToolLocator)
        : this(
            new FFprobeMediaInspector(mediaToolLocator),
            new FFmpegMediaPipeline(mediaToolLocator),
            new WhisperTranscriptionService(),
            new TranscriptExporter(),
            new JobWorkspaceManager())
    {
    }

    internal ProcessingService(
        IMediaInspector mediaInspector,
        IMediaConversionService mediaConverter,
        IWhisperTranscriptionService transcriber,
        ITranscriptExporter transcriptExporter,
        IJobWorkspaceManager workspaceManager)
    {
        this.mediaInspector = mediaInspector
            ?? throw new ArgumentNullException(nameof(mediaInspector));
        this.mediaConverter = mediaConverter
            ?? throw new ArgumentNullException(nameof(mediaConverter));
        this.transcriber = transcriber
            ?? throw new ArgumentNullException(nameof(transcriber));
        this.transcriptExporter = transcriptExporter
            ?? throw new ArgumentNullException(nameof(transcriptExporter));
        this.workspaceManager = workspaceManager
            ?? throw new ArgumentNullException(nameof(workspaceManager));
    }

    public Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref isRunning, 1, 0) != 0)
        {
            return Task.FromException<ProcessingResult>(
                new ProcessingException(
                    ProcessingError.ProcessingInProgress));
        }

        return ExecuteWithLifetimeAsync(request, progress, cancellationToken);
    }

    private async Task<ProcessingResult> ExecuteWithLifetimeAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var progressRelay = new ProcessingProgressRelay(progress);
        JobWorkspace? workspace = null;
        ProcessingResult? result = null;
        Exception? failure = null;
        try
        {
            try
            {
                Guid jobId = Guid.NewGuid();
                PreparedJob prepared = await PreflightAsync(
                        request,
                        jobId,
                        progressRelay,
                        cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    workspace = workspaceManager.Create(jobId);
                }
                catch
                {
                    throw new ProcessingException(
                        ProcessingError.PreflightFailed);
                }

                result = await ExecutePreparedAsync(
                        prepared,
                        workspace,
                        progressRelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                failure = new OperationCanceledException(cancellationToken);
            }
            catch (ProcessingException exception)
            {
                failure = exception;
            }
            catch
            {
                failure = new ProcessingException(
                    ProcessingError.PreflightFailed);
            }

            if (workspace is not null)
            {
                try
                {
                    workspaceManager.Cleanup(workspace);
                    workspace = null;
                }
                catch
                {
                    failure = new ProcessingException(
                        ProcessingError.CleanupFailed);
                }
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            progressRelay.Finish();
            return result
                ?? throw new ProcessingException(
                    ProcessingError.PreflightFailed);
        }
        finally
        {
            if (workspace is not null)
            {
                try
                {
                    workspaceManager.Cleanup(workspace);
                }
                catch when (failure is not null)
                {
                }
            }

            Volatile.Write(ref isRunning, 0);
        }
    }

    private async Task<PreparedJob> PreflightAsync(
        ProcessingRequest request,
        Guid jobId,
        ProcessingProgressRelay progress,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !Enum.IsDefined(request.Mode)
            || !Enum.IsDefined(request.Language))
        {
            throw new ProcessingException(ProcessingError.InvalidInput);
        }

        progress.Report(ProcessingPhase.Preflight, 0.01);
        cancellationToken.ThrowIfCancellationRequested();
        string inputPath = RequireReadableFile(
            request.InputMediaPath,
            ProcessingError.InvalidInput);
        string resultsDirectory = RequireResultsDirectory(
            request.ResultsDirectory);
        string? modelPath = null;
        if (IncludesText(request.Mode))
        {
            modelPath = RequireReadableFile(
                request.ModelPath,
                ProcessingError.ModelUnavailableOrInvalid);
        }

        string baseName = Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Результат";
        }

        var plan = new OutputPlan(
            request.Mode,
            resultsDirectory,
            baseName);
        RequireOutputsAbsent(plan.FinalPaths);

        MediaMetadata metadata;
        try
        {
            metadata = await mediaInspector
                .InspectAsync(inputPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MediaInspectionException exception)
            when (exception.Error == MediaInspectionError.NoAudioStream)
        {
            throw new ProcessingException(ProcessingError.NoAudioStream);
        }
        catch (MediaInspectionException exception)
            when (exception.Error
                == MediaInspectionError.InvalidOrUnsupportedMedia)
        {
            throw new ProcessingException(ProcessingError.InvalidInput);
        }
        catch
        {
            throw new ProcessingException(ProcessingError.PreflightFailed);
        }

        progress.Report(ProcessingPhase.Preflight, 0.05);
        return new PreparedJob(
            jobId,
            inputPath,
            modelPath,
            request.Language,
            plan,
            metadata.Duration);
    }

    private async Task<ProcessingResult> ExecutePreparedAsync(
        PreparedJob job,
        JobWorkspace workspace,
        ProcessingProgressRelay progress,
        CancellationToken cancellationToken)
    {
        string stagedMp3 = Path.Combine(workspace.Path, "audio.mp3");
        string temporaryWav = Path.Combine(
            workspace.Path,
            "audio-16khz-mono.wav");

        if (job.Plan.Mode == ProcessingMode.AudioOnly)
        {
            await ConvertMp3Async(
                    job,
                    stagedMp3,
                    progress.Map(ProcessingPhase.MediaProcessing, 0.05, 0.90),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (job.Plan.Mode == ProcessingMode.TextOnly)
        {
            await ConvertWavAsync(
                    job,
                    temporaryWav,
                    progress.Map(ProcessingPhase.MediaProcessing, 0.05, 0.35),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await ConvertMp3Async(
                    job,
                    stagedMp3,
                    progress.Map(ProcessingPhase.MediaProcessing, 0.05, 0.20),
                    cancellationToken)
                .ConfigureAwait(false);
            await ConvertWavAsync(
                    job,
                    temporaryWav,
                    progress.Map(ProcessingPhase.MediaProcessing, 0.20, 0.35),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        TranscriptionResult? transcription = null;
        StagedTranscript? stagedTranscript = null;
        if (IncludesText(job.Plan.Mode))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(ProcessingPhase.Transcription, 0.40);
            try
            {
                transcription = await transcriber.TranscribeAsync(
                        new TranscriptionRequest(
                            job.ModelPath!,
                            temporaryWav,
                            job.Language),
                        progress.Map(
                            ProcessingPhase.Transcription,
                            0.40,
                            0.85),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TranscriptionException exception)
                when (exception.Error == TranscriptionError.InvalidModel)
            {
                throw new ProcessingException(
                    ProcessingError.ModelUnavailableOrInvalid);
            }
            catch
            {
                throw new ProcessingException(
                    ProcessingError.TranscriptionFailed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(ProcessingPhase.Exporting, 0.88);
            try
            {
                stagedTranscript = await transcriptExporter.StageAsync(
                        transcription,
                        workspace,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new ProcessingException(ProcessingError.ExportFailed);
            }

            progress.Report(ProcessingPhase.Exporting, 0.92);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(ProcessingPhase.Publishing, 0.96);
        IReadOnlyList<Artifact> artifacts = BuildPublicationArtifacts(
            job,
            stagedMp3,
            stagedTranscript);
        await PublishAsync(
                job.JobId,
                artifacts,
                cancellationToken)
            .ConfigureAwait(false);
        progress.Report(ProcessingPhase.Publishing, 0.99);
        return new ProcessingResult(
            job.JobId,
            job.Plan.FinalPaths.ToArray(),
            transcription);
    }

    private async Task ConvertMp3Async(
        PreparedJob job,
        string outputPath,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await mediaConverter.ConvertToMp3Async(
                    job.InputPath,
                    outputPath,
                    job.Duration,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new ProcessingException(
                ProcessingError.MediaProcessingFailed);
        }
    }

    private async Task ConvertWavAsync(
        PreparedJob job,
        string outputPath,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await mediaConverter.ConvertToWhisperWavAsync(
                    job.InputPath,
                    outputPath,
                    job.Duration,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new ProcessingException(
                ProcessingError.MediaProcessingFailed);
        }
    }

    private static IReadOnlyList<Artifact> BuildPublicationArtifacts(
        PreparedJob job,
        string stagedMp3,
        StagedTranscript? transcript)
    {
        var artifacts = new List<Artifact>();
        if (transcript is not null)
        {
            artifacts.Add(new Artifact(
                transcript.TxtPath,
                job.Plan.TxtPath!));
            artifacts.Add(new Artifact(
                transcript.SrtPath,
                job.Plan.SrtPath!));
        }
        if (job.Plan.Mp3Path is not null)
        {
            artifacts.Add(new Artifact(stagedMp3, job.Plan.Mp3Path));
        }

        return artifacts;
    }

    private static async Task PublishAsync(
        Guid jobId,
        IReadOnlyList<Artifact> artifacts,
        CancellationToken cancellationToken)
    {
        RequireOutputsAbsent(artifacts.Select(item => item.FinalPath));
        var prepared = new List<PreparedPublication>();
        var published = new List<string>();
        Exception? primaryFailure = null;
        try
        {
            foreach (Artifact artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireStagedFile(artifact.StagedPath);
                string directory = Path.GetDirectoryName(artifact.FinalPath)!;
                string partialPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(artifact.FinalPath)}."
                    + $"{jobId:N}.publish.partial");
                await CopyNewAsync(
                        artifact.StagedPath,
                        partialPath,
                        () => prepared.Add(new PreparedPublication(
                            partialPath,
                            artifact.FinalPath)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            RequireOutputsAbsent(artifacts.Select(item => item.FinalPath));
            foreach (PreparedPublication item in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(item.PartialPath, item.FinalPath, overwrite: false);
                published.Add(item.FinalPath);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            primaryFailure = new OperationCanceledException(cancellationToken);
        }
        catch (ProcessingException exception)
        {
            primaryFailure = exception;
        }
        catch (IOException) when (
            artifacts.Any(item =>
                PathExists(item.FinalPath)
                && !published.Contains(
                    item.FinalPath,
                    StringComparer.OrdinalIgnoreCase)))
        {
            primaryFailure = new ProcessingException(
                ProcessingError.OutputConflict);
        }
        catch
        {
            primaryFailure = new ProcessingException(
                ProcessingError.ExportFailed);
        }

        bool cleanupFailed = false;
        foreach (PreparedPublication item in prepared)
        {
            cleanupFailed |= !TryDelete(item.PartialPath);
        }
        if (primaryFailure is not null)
        {
            foreach (string path in published.AsEnumerable().Reverse())
            {
                cleanupFailed |= !TryDelete(path);
            }
        }
        if (cleanupFailed)
        {
            throw new ProcessingException(ProcessingError.CleanupFailed);
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private static async Task CopyNewAsync(
        string sourcePath,
        string destinationPath,
        Action created,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        created();
        await source.CopyToAsync(destination, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string RequireReadableFile(
        string? path,
        ProcessingError error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ProcessingException(error);
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            file.Refresh();
            if (!file.Exists
                || (file.Attributes & FileAttributes.Directory) != 0
                || file.Length <= 0)
            {
                throw new ProcessingException(error);
            }

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return fullPath;
        }
        catch (ProcessingException)
        {
            throw;
        }
        catch
        {
            throw new ProcessingException(error);
        }
    }

    private static string RequireResultsDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ProcessingException(ProcessingError.PreflightFailed);
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            var directory = new DirectoryInfo(fullPath);
            directory.Refresh();
            if (!directory.Exists
                || (directory.Attributes & FileAttributes.Directory) == 0)
            {
                throw new ProcessingException(
                    ProcessingError.PreflightFailed);
            }

            return fullPath;
        }
        catch (ProcessingException)
        {
            throw;
        }
        catch
        {
            throw new ProcessingException(ProcessingError.PreflightFailed);
        }
    }

    private static void RequireOutputsAbsent(IEnumerable<string> paths)
    {
        if (paths.Any(PathExists))
        {
            throw new ProcessingException(ProcessingError.OutputConflict);
        }
    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static void RequireStagedFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists
                || (file.Attributes & FileAttributes.Directory) != 0)
            {
                throw new ProcessingException(ProcessingError.ExportFailed);
            }
        }
        catch (ProcessingException)
        {
            throw;
        }
        catch
        {
            throw new ProcessingException(ProcessingError.ExportFailed);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IncludesText(ProcessingMode mode) =>
        mode is ProcessingMode.TextOnly or ProcessingMode.AudioAndText;

    private sealed class ProcessingProgressRelay
    {
        private readonly object gate = new();
        private readonly IProgress<ProcessingProgress>? progress;
        private double lastFraction;
        private ProcessingPhase? lastPhase;
        private bool finished;

        internal ProcessingProgressRelay(
            IProgress<ProcessingProgress>? progress)
        {
            this.progress = progress;
        }

        internal IProgress<double> Map(
            ProcessingPhase phase,
            double start,
            double end) =>
            new MappedProgress(
                value => Report(
                    phase,
                    start + ((end - start) * value)));

        internal void Report(ProcessingPhase phase, double candidate)
        {
            ProcessingProgress value;
            lock (gate)
            {
                if (finished || !double.IsFinite(candidate))
                {
                    return;
                }

                double fraction = Math.Clamp(
                    candidate,
                    lastFraction,
                    0.999_999);
                if (fraction == lastFraction && phase == lastPhase)
                {
                    return;
                }

                lastFraction = fraction;
                lastPhase = phase;
                value = new ProcessingProgress(phase, fraction);
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
                lastFraction = 1.0;
                lastPhase = ProcessingPhase.Completed;
            }

            ReportSafely(new ProcessingProgress(
                ProcessingPhase.Completed,
                1.0));
        }

        private void ReportSafely(ProcessingProgress value)
        {
            try
            {
                progress?.Report(value);
            }
            catch
            {
                // Progress is observational and cannot fail processing.
            }
        }
    }

    private sealed class MappedProgress : IProgress<double>
    {
        private readonly Action<double> report;

        internal MappedProgress(Action<double> report)
        {
            this.report = report;
        }

        public void Report(double value)
        {
            if (double.IsFinite(value))
            {
                report(Math.Clamp(value, 0.0, 1.0));
            }
        }
    }

    private sealed record OutputPlan(
        ProcessingMode Mode,
        string ResultsDirectory,
        string BaseName)
    {
        internal string? Mp3Path => Mode == ProcessingMode.TextOnly
            ? null
            : Path.Combine(ResultsDirectory, $"{BaseName}.mp3");

        internal string? TxtPath => Mode == ProcessingMode.AudioOnly
            ? null
            : Path.Combine(ResultsDirectory, $"{BaseName}.txt");

        internal string? SrtPath => Mode == ProcessingMode.AudioOnly
            ? null
            : Path.Combine(ResultsDirectory, $"{BaseName}.srt");

        internal IReadOnlyList<string> FinalPaths =>
            new[] { Mp3Path, TxtPath, SrtPath }
                .OfType<string>()
                .ToArray();
    }

    private sealed record PreparedJob(
        Guid JobId,
        string InputPath,
        string? ModelPath,
        TranscriptionLanguage Language,
        OutputPlan Plan,
        TimeSpan? Duration);

    private sealed record Artifact(string StagedPath, string FinalPath);

    private sealed record PreparedPublication(
        string PartialPath,
        string FinalPath);
}
