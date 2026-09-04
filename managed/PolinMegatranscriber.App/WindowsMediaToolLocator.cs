using PolinMegatranscriber.Core;
using System.IO;

namespace PolinMegatranscriber.App;

internal sealed class WindowsMediaToolLocator : IMediaToolLocator
{
    internal const string DevelopmentEnvironmentVariable =
        "POLIN_MEGATRANSCRIBER_MEDIA_TOOLS";

    public MediaToolPaths Locate()
    {
        foreach (string directory in CandidateDirectories())
        {
            string ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            string ffprobe = Path.Combine(directory, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                return new MediaToolPaths(ffmpeg, ffprobe);
            }
        }

        throw new FileNotFoundException(
            "FFmpeg не найден. Укажите папку инструментов в "
            + DevelopmentEnvironmentVariable + ".");
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "Runtime",
            "MediaTools");

        string? configured = Environment.GetEnvironmentVariable(
            DevelopmentEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string? fullPath = TryGetFullPath(configured);
            if (fullPath is not null)
            {
                yield return fullPath;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string entry in path.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries
                         | StringSplitOptions.TrimEntries))
            {
                yield return entry.Trim('"');
            }
        }

        yield return @"C:\ffmpeg\bin";
        yield return @"C:\ffmpeg";
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return null;
        }
    }
}
