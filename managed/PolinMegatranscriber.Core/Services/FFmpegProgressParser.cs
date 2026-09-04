using System.Globalization;
using System.Text;

namespace PolinMegatranscriber.Core;

internal enum FFmpegProgressEventKind
{
    Fraction,
    End,
}

internal readonly record struct FFmpegProgressEvent(
    FFmpegProgressEventKind Kind,
    double Fraction);

internal sealed class FFmpegProgressParser
{
    private readonly double? durationSeconds;
    private double fraction;

    internal FFmpegProgressParser(TimeSpan? duration)
    {
        if (duration is { } value
            && value > TimeSpan.Zero
            && double.IsFinite(value.TotalSeconds))
        {
            durationSeconds = value.TotalSeconds;
        }
    }

    internal FFmpegProgressEvent? ConsumeLine(string line)
    {
        int separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return null;
        }

        string key = line[..separator].Trim();
        string value = line[(separator + 1)..].Trim();
        if (key == "progress" && value == "end")
        {
            fraction = 1.0;
            return new FFmpegProgressEvent(
                FFmpegProgressEventKind.End,
                fraction);
        }

        if (key != "out_time_us"
            || durationSeconds is not { } duration
            || !double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double microseconds)
            || !double.IsFinite(microseconds)
            || microseconds < 0)
        {
            return null;
        }

        double candidate = Math.Clamp(
            microseconds / 1_000_000.0 / duration,
            0.0,
            1.0);
        fraction = Math.Max(fraction, candidate);
        return new FFmpegProgressEvent(
            FFmpegProgressEventKind.Fraction,
            fraction);
    }
}

internal sealed class FFmpegProgressStreamReader
{
    private const int MaximumLineLength = 4 * 1024;

    private readonly FFmpegProgressParser parser;
    private readonly Action<FFmpegProgressEvent> handler;
    private readonly StringBuilder pendingLine = new();
    private bool discardingOversizedLine;

    internal FFmpegProgressStreamReader(
        TimeSpan? duration,
        Action<FFmpegProgressEvent> handler)
    {
        parser = new FFmpegProgressParser(duration);
        this.handler = handler;
    }

    internal void Consume(string chunk)
    {
        foreach (char character in chunk)
        {
            if (character == '\n')
            {
                if (!discardingOversizedLine)
                {
                    ConsumePendingLine();
                }

                pendingLine.Clear();
                discardingOversizedLine = false;
            }
            else if (discardingOversizedLine)
            {
                continue;
            }
            else if (pendingLine.Length < MaximumLineLength)
            {
                pendingLine.Append(character);
            }
            else
            {
                pendingLine.Clear();
                discardingOversizedLine = true;
            }
        }
    }

    internal void Complete()
    {
        if (!discardingOversizedLine && pendingLine.Length > 0)
        {
            ConsumePendingLine();
        }

        pendingLine.Clear();
        discardingOversizedLine = false;
    }

    private void ConsumePendingLine()
    {
        FFmpegProgressEvent? progressEvent = parser.ConsumeLine(
            pendingLine.ToString().TrimEnd('\r'));
        if (progressEvent is { } value)
        {
            handler(value);
        }
    }
}
