using System.Text;

namespace PolinMegatranscriber.Core;

internal sealed record JobWorkspace(
    Guid JobId,
    string RootPath,
    string Path,
    string MarkerPath);

internal interface IJobWorkspaceManager
{
    JobWorkspace Create(Guid jobId);

    void Cleanup(JobWorkspace workspace);
}

internal sealed class JobWorkspaceManager : IJobWorkspaceManager
{
    private const string MarkerName = ".polin-megatranscriber-workspace";

    private readonly string rootPath;

    internal JobWorkspaceManager(string? rootPath = null)
    {
        this.rootPath = Path.GetFullPath(
            rootPath ?? Path.Combine(
                Path.GetTempPath(),
                "PolinMegatranscriber",
                "Jobs"));
    }

    public JobWorkspace Create(Guid jobId)
    {
        string workspacePath = Path.Combine(rootPath, jobId.ToString("D"));
        string markerPath = Path.Combine(workspacePath, MarkerName);
        bool workspaceCreated = false;
        try
        {
            Directory.CreateDirectory(rootPath);
            if (Directory.Exists(workspacePath) || File.Exists(workspacePath))
            {
                throw new IOException("Workspace already exists.");
            }

            Directory.CreateDirectory(workspacePath);
            workspaceCreated = true;
            using var marker = new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            byte[] identity = Encoding.UTF8.GetBytes(jobId.ToString("D"));
            marker.Write(identity);
            return new JobWorkspace(
                jobId,
                rootPath,
                workspacePath,
                markerPath);
        }
        catch
        {
            TryRemoveCreatedWorkspace(workspacePath, workspaceCreated);
            throw;
        }
    }

    public void Cleanup(JobWorkspace workspace)
    {
        string expectedPath = Path.Combine(
            rootPath,
            workspace.JobId.ToString("D"));
        string expectedMarker = Path.Combine(expectedPath, MarkerName);
        if (!PathEquals(workspace.RootPath, rootPath)
            || !PathEquals(workspace.Path, expectedPath)
            || !PathEquals(workspace.MarkerPath, expectedMarker))
        {
            throw new IOException("Workspace is not owned by this manager.");
        }
        if (!Directory.Exists(expectedPath))
        {
            return;
        }

        var directory = new DirectoryInfo(expectedPath);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0
            || !File.Exists(expectedMarker)
            || (File.GetAttributes(expectedMarker)
                & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || File.ReadAllText(expectedMarker, Encoding.UTF8)
                != workspace.JobId.ToString("D"))
        {
            throw new IOException("Workspace ownership marker is invalid.");
        }

        Directory.Delete(expectedPath, recursive: true);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void TryRemoveCreatedWorkspace(
        string workspacePath,
        bool workspaceCreated)
    {
        try
        {
            if (workspaceCreated && Directory.Exists(workspacePath))
            {
                Directory.Delete(workspacePath, recursive: true);
            }
        }
        catch
        {
        }
    }
}
