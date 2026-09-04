using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolinMegatranscriber.Core;

internal sealed record ModelDescriptor(
    WhisperModel Id,
    string Filename,
    Uri DownloadUri,
    string Sha256,
    long SizeBytes,
    string DisplaySize)
{
    internal WhisperModelInfo ToInfo() => Id switch
    {
        WhisperModel.Small => new WhisperModelInfo(
            Id,
            "Быстрее",
            "Small · около 488 МБ",
            SizeBytes),
        WhisperModel.Medium => new WhisperModelInfo(
            Id,
            "Точнее",
            "Medium · около 1,5 ГБ",
            SizeBytes),
        _ => throw new ModelManagerException(
            ModelManagementError.ManifestUnavailable),
    };
}

internal sealed class ModelManifest
{
    private const string ResourceName =
        "PolinMegatranscriber.Core.whisper-models.json";

    private readonly IReadOnlyDictionary<WhisperModel, ModelDescriptor>
        descriptors;

    private ModelManifest(IEnumerable<ModelDescriptor> descriptors)
    {
        this.descriptors = descriptors.ToDictionary(item => item.Id);
        Models = ModelInfoCollection.Create(
            Enum.GetValues<WhisperModel>()
                .Select(model => this.descriptors[model].ToInfo()));
    }

    internal IReadOnlyList<WhisperModelInfo> Models { get; }

    internal ModelDescriptor Get(WhisperModel model)
    {
        if (!descriptors.TryGetValue(model, out ModelDescriptor? descriptor))
        {
            throw new ArgumentOutOfRangeException(nameof(model));
        }

        return descriptor;
    }

    internal static ModelManifest LoadEmbedded()
    {
        try
        {
            Assembly assembly = typeof(ModelManifest).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidDataException("Manifest resource is missing.");
            return Load(stream);
        }
        catch (ModelManagerException)
        {
            throw;
        }
        catch
        {
            throw new ModelManagerException(
                ModelManagementError.ManifestUnavailable);
        }
    }

    internal static ModelManifest Load(Stream json)
    {
        ManifestDto? source;
        try
        {
            source = JsonSerializer.Deserialize<ManifestDto>(json);
        }
        catch (JsonException)
        {
            throw new ModelManagerException(
                ModelManagementError.ManifestUnavailable);
        }

        if (source is null
            || source.SchemaVersion != 1
            || source.Source is null
            || string.IsNullOrWhiteSpace(source.Source.Repository)
            || string.IsNullOrWhiteSpace(source.Source.Revision)
            || string.IsNullOrWhiteSpace(source.Source.License)
            || source.Models is null
            || source.Models.Count != 2)
        {
            throw new ModelManagerException(
                ModelManagementError.ManifestUnavailable);
        }

        var descriptors = new List<ModelDescriptor>(2);
        var identifiers = new HashSet<WhisperModel>();
        foreach (ModelDto item in source.Models)
        {
            if (!TryParseModel(item.Id, out WhisperModel model)
                || !identifiers.Add(model)
                || !IsSafeFilename(item.Filename)
                || !Uri.TryCreate(
                    item.Url,
                    UriKind.Absolute,
                    out Uri? downloadUri)
                || downloadUri.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrWhiteSpace(downloadUri.Host)
                || item.SizeBytes <= 0
                || !IsSha256(item.Sha256)
                || string.IsNullOrWhiteSpace(item.DisplaySize))
            {
                throw new ModelManagerException(
                    ModelManagementError.ManifestUnavailable);
            }

            descriptors.Add(new ModelDescriptor(
                model,
                item.Filename!,
                downloadUri,
                item.Sha256!,
                item.SizeBytes,
                item.DisplaySize!));
        }

        if (!identifiers.SetEquals(Enum.GetValues<WhisperModel>()))
        {
            throw new ModelManagerException(
                ModelManagementError.ManifestUnavailable);
        }

        return new ModelManifest(descriptors);
    }

    private static bool TryParseModel(
        string? value,
        out WhisperModel model)
    {
        if (string.Equals(value, "small", StringComparison.Ordinal))
        {
            model = WhisperModel.Small;
            return true;
        }
        if (string.Equals(value, "medium", StringComparison.Ordinal))
        {
            model = WhisperModel.Medium;
            return true;
        }

        model = default;
        return false;
    }

    private static bool IsSafeFilename(string? filename) =>
        !string.IsNullOrWhiteSpace(filename)
        && !Path.IsPathRooted(filename)
        && Path.GetFileName(filename) == filename
        && !filename.Contains("..", StringComparison.Ordinal)
        && filename.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private sealed class ManifestDto
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("source")]
        public SourceDto? Source { get; init; }

        [JsonPropertyName("models")]
        public List<ModelDto>? Models { get; init; }
    }

    private sealed class SourceDto
    {
        [JsonPropertyName("repository")]
        public string? Repository { get; init; }

        [JsonPropertyName("revision")]
        public string? Revision { get; init; }

        [JsonPropertyName("license")]
        public string? License { get; init; }
    }

    private sealed class ModelDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("filename")]
        public string? Filename { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; init; }

        [JsonPropertyName("displaySize")]
        public string? DisplaySize { get; init; }
    }
}
