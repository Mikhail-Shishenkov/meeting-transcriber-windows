using System.Globalization;
using System.Text;

namespace PolinMegatranscriber.Core;

internal sealed record StagedTranscript(string TxtPath, string SrtPath);

internal interface ITranscriptExporter
{
    Task<StagedTranscript> StageAsync(
        TranscriptionResult transcription,
        JobWorkspace workspace,
        CancellationToken cancellationToken = default);
}

internal sealed class TranscriptExporter : ITranscriptExporter
{
    public async Task<StagedTranscript> StageAsync(
        TranscriptionResult transcription,
        JobWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcription);
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateSegments(transcription.Segments);
        cancellationToken.ThrowIfCancellationRequested();

        string txtPath = Path.Combine(workspace.Path, "transcript.txt");
        string srtPath = Path.Combine(workspace.Path, "transcript.srt");
        bool txtCreated = false;
        bool srtCreated = false;
        try
        {
            await WriteNewUtf8Async(
                    txtPath,
                    FormatTxt(transcription.Segments),
                    () => txtCreated = true,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteNewUtf8Async(
                    srtPath,
                    FormatSrt(transcription.Segments),
                    () => srtCreated = true,
                    cancellationToken)
                .ConfigureAwait(false);
            return new StagedTranscript(txtPath, srtPath);
        }
        catch
        {
            DeleteIfCreated(srtPath, srtCreated);
            DeleteIfCreated(txtPath, txtCreated);
            throw;
        }
    }

    internal static string FormatTxt(
        IReadOnlyList<TranscriptionSegment> segments)
    {
        ValidateSegments(segments);
        return string.Join('\n', segments.Select(segment => segment.Text));
    }

    internal static string FormatSrt(
        IReadOnlyList<TranscriptionSegment> segments)
    {
        ValidateSegments(segments);
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        for (int index = 0; index < segments.Count; index++)
        {
            TranscriptionSegment segment = segments[index];
            if (index > 0)
            {
                result.Append("\n\n");
            }

            result.Append(index + 1);
            result.Append('\n');
            result.Append(FormatTimestamp(segment.StartMilliseconds));
            result.Append(" --> ");
            result.Append(FormatTimestamp(segment.EndMilliseconds));
            result.Append('\n');
            result.Append(segment.Text);
        }

        result.Append('\n');
        return result.ToString();
    }

    private static string FormatTimestamp(long milliseconds)
    {
        long hours = milliseconds / 3_600_000;
        long minutes = (milliseconds / 60_000) % 60;
        long seconds = (milliseconds / 1_000) % 60;
        long remainder = milliseconds % 1_000;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{seconds:00},{remainder:000}");
    }

    private static void ValidateSegments(
        IReadOnlyList<TranscriptionSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        long? previousStart = null;
        foreach (TranscriptionSegment segment in segments)
        {
            if (segment is null
                || segment.StartMilliseconds < 0
                || segment.EndMilliseconds < segment.StartMilliseconds
                || (previousStart is not null
                    && segment.StartMilliseconds < previousStart.Value))
            {
                throw new InvalidDataException(
                    "Transcription segments are not chronological.");
            }

            previousStart = segment.StartMilliseconds;
        }
    }

    private static async Task WriteNewUtf8Async(
        string path,
        string content,
        Action created,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        created();
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteIfCreated(string path, bool created)
    {
        if (created && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
