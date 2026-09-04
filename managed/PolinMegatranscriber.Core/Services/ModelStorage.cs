namespace PolinMegatranscriber.Core;

internal sealed class ModelStorage
{
    internal ModelStorage(string? applicationRoot = null)
    {
        ApplicationRoot = Path.GetFullPath(
            applicationRoot ?? DefaultApplicationRoot());
        ModelsDirectory = Path.Combine(ApplicationRoot, "Models");
        DownloadsDirectory = Path.Combine(ApplicationRoot, "Downloads");
    }

    internal string ApplicationRoot { get; }

    internal string ModelsDirectory { get; }

    internal string DownloadsDirectory { get; }

    internal string ModelPath(ModelDescriptor descriptor) =>
        ControlledPath(ModelsDirectory, descriptor.Filename);

    internal string PartialPath(
        WhisperModel model,
        Guid operationId) =>
        ControlledPath(
            DownloadsDirectory,
            $".polin-model-{model.ToString().ToLowerInvariant()}-"
            + $"{operationId:N}.partial");

    internal string StagingPath(
        ModelDescriptor descriptor,
        Guid operationId) =>
        ControlledPath(
            ModelsDirectory,
            $".{descriptor.Filename}.{operationId:N}.installing");

    internal void EnsureDirectories()
    {
        EnsureControlledDirectory(ApplicationRoot);
        EnsureControlledDirectory(ModelsDirectory);
        EnsureControlledDirectory(DownloadsDirectory);
    }

    internal bool ModelsDirectoryIsAvailable()
    {
        if (!Directory.Exists(ModelsDirectory))
        {
            return false;
        }

        ValidateControlledDirectory(ModelsDirectory);
        return true;
    }

    internal bool IsKnownModelPath(
        string candidate,
        ModelDescriptor descriptor) =>
        PathEquals(candidate, ModelPath(descriptor));

    private static string ControlledPath(string directory, string filename)
    {
        string candidate = Path.GetFullPath(Path.Combine(directory, filename));
        if (!PathEquals(Path.GetDirectoryName(candidate), directory))
        {
            throw new ModelManagerException(
                ModelManagementError.InvalidInstallationTarget);
        }

        return candidate;
    }

    private static void EnsureControlledDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            ValidateControlledDirectory(path);
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
    }

    private static void ValidateControlledDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            directory.Refresh();
            if (!directory.Exists
                || (directory.Attributes & FileAttributes.Directory) == 0
                || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
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
                ModelManagementError.StorageUnavailable);
        }
    }

    private static bool PathEquals(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string DefaultApplicationRoot()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new ModelManagerException(
                ModelManagementError.StorageUnavailable);
        }

        return Path.Combine(localApplicationData, "PolinMegatranscriber");
    }
}
