using System.Runtime.InteropServices;
using PolinMegatranscriber.Native;

try
{
    if (!WhisperRuntimeAvailability.IsRuntimeAvailable())
    {
        return Fail(
            1,
            "pmt_whisper_runtime_available() reports the runtime as unavailable.");
    }

    Console.WriteLine("Smoke OK: native Whisper runtime is available.");
    VerifyManagedStatusValues();
    VerifyMissingModelContract();

    SmokeArguments options = SmokeArguments.Parse(args);
    if (options.ModelPath is null)
    {
        Console.WriteLine(
            "Smoke OK: model-free checks passed; real inference was not requested.");
        return 0;
    }

    return RunRealInference(options);
}
catch (Exception ex) when (
    ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
{
    return Fail(2, $"Could not call pmtwhisper.dll: {ex.Message}");
}
catch (Exception ex)
{
    return Fail(3, ex.ToString());
}

static void VerifyMissingModelContract()
{
    string missingModel = Path.Combine(
        Path.GetTempPath(),
        $"pmt-missing-model-{Guid.NewGuid():N}.bin");
    if (File.Exists(missingModel))
    {
        throw new InvalidOperationException(
            "Generated missing-model path unexpectedly exists.");
    }

    try
    {
        using WhisperSession unexpected = WhisperSession.Create(missingModel);
        throw new InvalidOperationException(
            "Creating a session for a nonexistent model unexpectedly succeeded.");
    }
    catch (WhisperException exception)
        when (exception.Status == WhisperStatus.ModelLoadFailed)
    {
        Console.WriteLine(
            "Smoke OK: nonexistent model returns ModelLoadFailed (3) and no session.");
    }
}

static void VerifyManagedStatusValues()
{
    WhisperStatus[] expected =
    [
        WhisperStatus.Ok,
        WhisperStatus.InvalidArgument,
        WhisperStatus.RuntimeUnavailable,
        WhisperStatus.ModelLoadFailed,
        WhisperStatus.InvalidWav,
        WhisperStatus.UnsupportedWav,
        WhisperStatus.InferenceFailed,
        WhisperStatus.Cancelled,
        WhisperStatus.InvalidResult,
    ];
    for (int value = 0; value < expected.Length; value++)
    {
        if ((int)expected[value] != value)
        {
            throw new InvalidOperationException(
                $"Managed Whisper status mismatch at value {value}.");
        }
    }

    Console.WriteLine("Smoke OK: managed status values exactly match native values 0..8.");
}

static int RunRealInference(SmokeArguments options)
{
    if (!File.Exists(options.ModelPath))
    {
        return Fail(4, $"Model does not exist: {options.ModelPath}");
    }

    if (!File.Exists(options.WavPath))
    {
        return Fail(5, $"WAV does not exist: {options.WavPath}");
    }

    var progressValues = new List<float>();
    WhisperTranscriptionResult result;
    using (WhisperSession session = WhisperSession.Create(options.ModelPath))
    {
        result = session.TranscribeWav(
            options.WavPath,
            options.Language,
            progress: value => progressValues.Add(value));
    }

    if (result.Segments.Count == 0)
    {
        return Fail(6, "Real inference returned no segments.");
    }

    foreach (WhisperSegment segment in result.Segments)
    {
        if (segment.StartMilliseconds < 0
            || segment.EndMilliseconds < segment.StartMilliseconds)
        {
            return Fail(7, "Real inference returned invalid timestamps.");
        }

        if (string.IsNullOrWhiteSpace(segment.Text))
        {
            return Fail(8, "Real inference returned an empty UTF-8 segment.");
        }
    }

    if (progressValues.Count == 0
        || progressValues[^1] != 1.0F
        || progressValues.Any(value => value <= 0 || value > 1)
        || progressValues.Zip(progressValues.Skip(1), (left, right) => right > left)
            .Any(increasing => !increasing))
    {
        return Fail(9, "Progress callback values were not sane and monotonic.");
    }

    using (WhisperSession cancellationSession =
        WhisperSession.Create(options.ModelPath))
    {
        cancellationSession.RequestCancellation();
        try
        {
            _ = cancellationSession.TranscribeWav(
                options.WavPath,
                options.Language);
            return Fail(10, "Cancellation request was not honored.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(
                "Real smoke OK: cancellation request returned Cancelled (7).");
        }
    }

    Console.WriteLine(
        $"Real smoke OK: {result.Segments.Count} segment(s), "
        + $"language={result.DetectedLanguage ?? "unknown"}, "
        + $"progress callbacks={progressValues.Count}, session disposed.");
    foreach (WhisperSegment segment in result.Segments)
    {
        Console.WriteLine(
            $"[{segment.StartMilliseconds}..{segment.EndMilliseconds} ms]"
            + segment.Text);
    }

    return 0;
}

static int Fail(int exitCode, string message)
{
    Console.Error.WriteLine($"Smoke FAILED: {message}");
    return exitCode;
}

internal sealed record SmokeArguments(
    string? ModelPath,
    string? WavPath,
    string Language)
{
    internal static SmokeArguments Parse(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            return new SmokeArguments(null, null, "auto");
        }

        string? modelPath = null;
        string? wavPath = null;
        string language = "auto";
        for (int index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length)
            {
                throw UsageError();
            }

            switch (arguments[index])
            {
                case "--model":
                    modelPath = arguments[index + 1];
                    break;
                case "--wav":
                    wavPath = arguments[index + 1];
                    break;
                case "--language":
                    language = arguments[index + 1];
                    break;
                default:
                    throw UsageError();
            }
        }

        if (string.IsNullOrWhiteSpace(modelPath)
            || string.IsNullOrWhiteSpace(wavPath)
            || string.IsNullOrWhiteSpace(language))
        {
            throw UsageError();
        }

        return new SmokeArguments(modelPath, wavPath, language);
    }

    private static ArgumentException UsageError() => new(
        "Usage: [--model <path> --wav <path> [--language <code>]]");
}
