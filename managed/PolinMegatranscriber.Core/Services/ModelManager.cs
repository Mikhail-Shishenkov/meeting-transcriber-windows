namespace PolinMegatranscriber.Core;

public sealed class ModelManager : IModelManager
{
    private readonly ModelManifest manifest;
    private readonly ModelStorage storage;
    private readonly IModelFileVerifier verifier;
    private readonly IModelDownloader downloader;
    private int mutationIsRunning;

    public ModelManager()
        : this(
            ModelManifest.LoadEmbedded(),
            new ModelStorage(),
            new StreamingModelFileVerifier(),
            new HttpModelDownloader())
    {
    }

    internal ModelManager(
        ModelManifest manifest,
        ModelStorage storage,
        IModelFileVerifier verifier,
        IModelDownloader downloader)
    {
        this.manifest = manifest
            ?? throw new ArgumentNullException(nameof(manifest));
        this.storage = storage
            ?? throw new ArgumentNullException(nameof(storage));
        this.verifier = verifier
            ?? throw new ArgumentNullException(nameof(verifier));
        this.downloader = downloader
            ?? throw new ArgumentNullException(nameof(downloader));
    }

    public IReadOnlyList<WhisperModelInfo> Models => manifest.Models;

    public string ModelsDirectory => storage.ModelsDirectory;

    public async Task<ModelInstallationStatus> GetStatusAsync(
        WhisperModel model,
        CancellationToken cancellationToken = default)
    {
        ModelDescriptor descriptor = Descriptor(model);
        cancellationToken.ThrowIfCancellationRequested();
        bool storageExists;
        try
        {
            storageExists = storage.ModelsDirectoryIsAvailable();
        }
        catch (ModelManagerException)
        {
            throw;
        }
        catch
        {
            throw new ModelManagerException(
                ModelManagementError.StorageUnavailable);
        }

        if (!storageExists)
        {
            return ModelInstallationStatus.Absent;
        }

        string path = storage.ModelPath(descriptor);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return ModelInstallationStatus.Absent;
        }

        try
        {
            await verifier.VerifyAsync(path, descriptor, cancellationToken)
                .ConfigureAwait(false);
            return ModelInstallationStatus.Verified;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ModelInstallationStatus.Corrupted;
        }
    }

    public async Task<string?> GetVerifiedPathAsync(
        WhisperModel model,
        CancellationToken cancellationToken = default)
    {
        ModelDescriptor descriptor = Descriptor(model);
        return await GetStatusAsync(model, cancellationToken)
                .ConfigureAwait(false)
            == ModelInstallationStatus.Verified
            ? storage.ModelPath(descriptor)
            : null;
    }

    public Task<string> DownloadAndInstallAsync(
        WhisperModel model,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(
                ref mutationIsRunning,
                1,
                0) != 0)
        {
            return Task.FromException<string>(
                new ModelManagerException(
                    ModelManagementError.InstallationInProgress));
        }

        return InstallWithLifetimeAsync(
            model,
            progress,
            cancellationToken);
    }

    public Task DeleteAsync(
        WhisperModel model,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref mutationIsRunning, 1, 0) != 0)
        {
            throw new ModelManagerException(
                ModelManagementError.InstallationInProgress);
        }

        try
        {
            ModelDescriptor descriptor = Descriptor(model);
            cancellationToken.ThrowIfCancellationRequested();
            if (!storage.ModelsDirectoryIsAvailable())
            {
                return Task.CompletedTask;
            }

            string path = storage.ModelPath(descriptor);
            if (!storage.IsKnownModelPath(path, descriptor))
            {
                throw new ModelManagerException(
                    ModelManagementError.InvalidInstallationTarget);
            }
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return Task.CompletedTask;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    throw new ModelManagerException(
                        ModelManagementError.InvalidInstallationTarget);
                }

                File.Delete(path);
                return Task.CompletedTask;
            }
            catch (ModelManagerException)
            {
                throw;
            }
            catch
            {
                throw new ModelManagerException(
                    ModelManagementError.DeletionFailed);
            }
        }
        finally
        {
            Volatile.Write(ref mutationIsRunning, 0);
        }
    }

    private async Task<string> InstallWithLifetimeAsync(
        WhisperModel model,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? partialPath = null;
        string? stagingPath = null;
        bool ownsPartial = false;
        bool ownsStaging = false;
        bool cleanupFailed = false;
        try
        {
            ModelDescriptor descriptor = Descriptor(model);
            var relay = new ModelProgressRelay(
                descriptor.SizeBytes,
                progress);
            if (await GetStatusAsync(model, cancellationToken)
                    .ConfigureAwait(false)
                == ModelInstallationStatus.Verified)
            {
                relay.Finish();
                return storage.ModelPath(descriptor);
            }

            try
            {
                storage.EnsureDirectories();
            }
            catch (ModelManagerException)
            {
                throw;
            }
            catch
            {
                throw new ModelManagerException(
                    ModelManagementError.StorageUnavailable);
            }

            Guid operationId = Guid.NewGuid();
            partialPath = storage.PartialPath(model, operationId);
            stagingPath = storage.StagingPath(descriptor, operationId);
            if (PathExists(partialPath) || PathExists(stagingPath))
            {
                throw new ModelManagerException(
                    ModelManagementError.InvalidInstallationTarget);
            }

            try
            {
                await downloader.DownloadAsync(
                        descriptor.DownloadUri,
                        partialPath,
                        descriptor.SizeBytes,
                        () => ownsPartial = true,
                        relay.Receive,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ModelDownloadException exception)
            {
                throw MapDownloadError(exception.Error);
            }
            catch
            {
                throw new ModelManagerException(
                    ModelManagementError.DownloadFailed);
            }

            await VerifyForInstallationAsync(
                    partialPath,
                    descriptor,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(partialPath, stagingPath, overwrite: false);
                ownsPartial = false;
                ownsStaging = true;
            }
            catch
            {
                throw new ModelManagerException(
                    ModelManagementError.InstallationFailed);
            }

            await VerifyForInstallationAsync(
                    stagingPath,
                    descriptor,
                    cancellationToken)
                .ConfigureAwait(false);
            string finalPath = storage.ModelPath(descriptor);
            if (await GetStatusAsync(model, cancellationToken)
                    .ConfigureAwait(false)
                == ModelInstallationStatus.Verified)
            {
                relay.Finish();
                return finalPath;
            }

            ValidateReplaceableFinal(finalPath, descriptor);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(stagingPath, finalPath, overwrite: true);
                ownsStaging = false;
            }
            catch
            {
                throw new ModelManagerException(
                    ModelManagementError.InstallationFailed);
            }

            relay.Finish();
            return finalPath;
        }
        finally
        {
            if (ownsPartial && partialPath is not null)
            {
                cleanupFailed |= !TryDeleteFile(partialPath);
            }
            if (ownsStaging && stagingPath is not null)
            {
                cleanupFailed |= !TryDeleteFile(stagingPath);
            }

            Volatile.Write(ref mutationIsRunning, 0);
            if (cleanupFailed)
            {
                throw new ModelManagerException(
                    ModelManagementError.CleanupFailed);
            }
        }
    }

    private async Task VerifyForInstallationAsync(
        string path,
        ModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        try
        {
            await verifier.VerifyAsync(path, descriptor, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new ModelManagerException(
                ModelManagementError.VerificationFailed);
        }
    }

    private void ValidateReplaceableFinal(
        string finalPath,
        ModelDescriptor descriptor)
    {
        if (!storage.IsKnownModelPath(finalPath, descriptor))
        {
            throw new ModelManagerException(
                ModelManagementError.InvalidInstallationTarget);
        }
        if (!PathExists(finalPath))
        {
            return;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(finalPath);
            if ((attributes
                & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new ModelManagerException(
                    ModelManagementError.InvalidInstallationTarget);
            }
        }
        catch (ModelManagerException)
        {
            throw;
        }
        catch
        {
            throw new ModelManagerException(
                ModelManagementError.InvalidInstallationTarget);
        }
    }

    private ModelDescriptor Descriptor(WhisperModel model)
    {
        try
        {
            return manifest.Get(model);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ModelManagerException(
                ModelManagementError.ManifestUnavailable);
        }
    }

    private static ModelManagerException MapDownloadError(
        ModelDownloadError error) => error switch
        {
            ModelDownloadError.InsecureSource
                or ModelDownloadError.InsecureRedirect =>
                new ModelManagerException(
                    ModelManagementError.InsecureDownload),
            ModelDownloadError.HttpFailure =>
                new ModelManagerException(ModelManagementError.HttpFailure),
            ModelDownloadError.NetworkFailure =>
                new ModelManagerException(ModelManagementError.NetworkFailure),
            ModelDownloadError.SizeExceeded =>
                new ModelManagerException(
                    ModelManagementError.VerificationFailed),
            _ => new ModelManagerException(
                ModelManagementError.DownloadFailed),
        };

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private sealed class ModelProgressRelay
    {
        private readonly object gate = new();
        private readonly long expectedBytes;
        private readonly IProgress<ModelDownloadProgress>? progress;
        private long downloadedBytes;
        private double fraction;
        private bool finished;

        internal ModelProgressRelay(
            long expectedBytes,
            IProgress<ModelDownloadProgress>? progress)
        {
            this.expectedBytes = expectedBytes;
            this.progress = progress;
        }

        internal void Receive(long receivedBytes)
        {
            ModelDownloadProgress value;
            lock (gate)
            {
                if (finished)
                {
                    return;
                }

                downloadedBytes = Math.Clamp(
                    receivedBytes,
                    downloadedBytes,
                    expectedBytes);
                fraction = Math.Clamp(
                    Math.Max(
                        fraction,
                        (double)downloadedBytes / expectedBytes * 0.95),
                    0.0,
                    0.95);
                value = new ModelDownloadProgress(
                    downloadedBytes,
                    expectedBytes,
                    fraction);
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
                downloadedBytes = expectedBytes;
                fraction = 1.0;
            }

            ReportSafely(new ModelDownloadProgress(
                expectedBytes,
                expectedBytes,
                1.0));
        }

        private void ReportSafely(ModelDownloadProgress value)
        {
            try
            {
                progress?.Report(value);
            }
            catch
            {
                // UI progress cannot fail model installation.
            }
        }
    }
}
