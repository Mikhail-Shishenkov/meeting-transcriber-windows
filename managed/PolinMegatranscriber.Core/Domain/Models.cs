using System.Collections.ObjectModel;

namespace PolinMegatranscriber.Core;

public enum WhisperModel
{
    Small,
    Medium,
}

public sealed record WhisperModelInfo(
    WhisperModel Id,
    string DisplayName,
    string Detail,
    long SizeBytes);

public enum ModelInstallationStatus
{
    Absent,
    Verified,
    Corrupted,
}

public readonly record struct ModelDownloadProgress(
    long DownloadedBytes,
    long ExpectedBytes,
    double Fraction);

public enum ModelManagementError
{
    ManifestUnavailable,
    InstallationInProgress,
    StorageUnavailable,
    InvalidInstallationTarget,
    InsecureDownload,
    HttpFailure,
    NetworkFailure,
    DownloadFailed,
    VerificationFailed,
    InstallationFailed,
    CleanupFailed,
    DeletionFailed,
}

public sealed class ModelManagerException : Exception
{
    internal ModelManagerException(ModelManagementError error)
        : base(MessageFor(error))
    {
        Error = error;
    }

    public ModelManagementError Error { get; }

    private static string MessageFor(ModelManagementError error) => error switch
    {
        ModelManagementError.ManifestUnavailable =>
            "Описание моделей отсутствует или повреждено.",
        ModelManagementError.InstallationInProgress =>
            "Установка модели уже выполняется.",
        ModelManagementError.StorageUnavailable =>
            "Хранилище моделей недоступно.",
        ModelManagementError.InvalidInstallationTarget =>
            "Небезопасный путь установки модели.",
        ModelManagementError.InsecureDownload =>
            "Загрузка модели должна использовать HTTPS.",
        ModelManagementError.HttpFailure =>
            "Сервер модели не смог выполнить запрос.",
        ModelManagementError.NetworkFailure =>
            "Не удалось загрузить модель из-за сетевой ошибки.",
        ModelManagementError.DownloadFailed =>
            "Не удалось сохранить загруженную модель.",
        ModelManagementError.VerificationFailed =>
            "Размер или контрольная сумма модели не совпадает с manifest.",
        ModelManagementError.InstallationFailed =>
            "Не удалось безопасно установить проверенную модель.",
        ModelManagementError.CleanupFailed =>
            "Не удалось удалить временный файл модели.",
        ModelManagementError.DeletionFailed =>
            "Не удалось удалить установленную модель.",
        _ => "Не удалось выполнить операцию с моделью.",
    };
}

public interface IModelManager
{
    IReadOnlyList<WhisperModelInfo> Models { get; }

    Task<ModelInstallationStatus> GetStatusAsync(
        WhisperModel model,
        CancellationToken cancellationToken = default);

    Task<string?> GetVerifiedPathAsync(
        WhisperModel model,
        CancellationToken cancellationToken = default);

    Task<string> DownloadAndInstallAsync(
        WhisperModel model,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        WhisperModel model,
        CancellationToken cancellationToken = default);
}

internal static class ModelInfoCollection
{
    internal static IReadOnlyList<WhisperModelInfo> Create(
        IEnumerable<WhisperModelInfo> models) =>
        new ReadOnlyCollection<WhisperModelInfo>(models.ToArray());
}
