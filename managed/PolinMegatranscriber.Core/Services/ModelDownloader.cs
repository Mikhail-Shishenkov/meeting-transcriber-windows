using System.Net;

namespace PolinMegatranscriber.Core;

internal enum ModelDownloadError
{
    InsecureSource,
    InsecureRedirect,
    InvalidResponse,
    HttpFailure,
    NetworkFailure,
    CannotStore,
    SizeExceeded,
}

internal sealed class ModelDownloadException : Exception
{
    internal ModelDownloadException(ModelDownloadError error)
    {
        Error = error;
    }

    internal ModelDownloadError Error { get; }
}

internal interface IModelDownloader
{
    Task DownloadAsync(
        Uri source,
        string destinationPath,
        long expectedBytes,
        Action destinationCreated,
        Action<long> bytesReceived,
        CancellationToken cancellationToken = default);
}

internal sealed class HttpModelDownloader : IModelDownloader
{
    private const int BufferSize = 1024 * 1024;
    private const int MaximumRedirects = 10;

    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient httpClient;

    internal HttpModelDownloader()
        : this(SharedClient)
    {
    }

    internal HttpModelDownloader(HttpClient httpClient)
    {
        this.httpClient = httpClient
            ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task DownloadAsync(
        Uri source,
        string destinationPath,
        long expectedBytes,
        Action destinationCreated,
        Action<long> bytesReceived,
        CancellationToken cancellationToken = default)
    {
        if (!IsHttps(source) || expectedBytes <= 0)
        {
            throw new ModelDownloadException(
                ModelDownloadError.InsecureSource);
        }

        Uri current = source;
        for (int redirects = 0; redirects <= MaximumRedirects; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                throw new ModelDownloadException(
                    ModelDownloadError.NetworkFailure);
            }

            using (response)
            {
                Uri finalRequestUri = response.RequestMessage?.RequestUri
                    ?? current;
                if (!IsHttps(finalRequestUri))
                {
                    throw new ModelDownloadException(
                        ModelDownloadError.InsecureRedirect);
                }
                if (IsRedirect(response.StatusCode))
                {
                    if (redirects == MaximumRedirects
                        || response.Headers.Location is not { } location)
                    {
                        throw new ModelDownloadException(
                            ModelDownloadError.InvalidResponse);
                    }

                    current = location.IsAbsoluteUri
                        ? location
                        : new Uri(current, location);
                    if (!IsHttps(current))
                    {
                        throw new ModelDownloadException(
                            ModelDownloadError.InsecureRedirect);
                    }

                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new ModelDownloadException(
                        ModelDownloadError.HttpFailure);
                }

                await CopyResponseAsync(
                        response,
                        destinationPath,
                        expectedBytes,
                        destinationCreated,
                        bytesReceived,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        throw new ModelDownloadException(ModelDownloadError.InvalidResponse);
    }

    private static async Task CopyResponseAsync(
        HttpResponseMessage response,
        string destinationPath,
        long expectedBytes,
        Action destinationCreated,
        Action<long> bytesReceived,
        CancellationToken cancellationToken)
    {
        Stream source;
        try
        {
            source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new ModelDownloadException(
                ModelDownloadError.NetworkFailure);
        }

        await using (source)
        {
            FileStream destination;
            try
            {
                destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                destinationCreated();
            }
            catch (ModelDownloadException)
            {
                throw;
            }
            catch
            {
                throw new ModelDownloadException(
                    ModelDownloadError.CannotStore);
            }

            await using (destination)
            {
                byte[] buffer = new byte[BufferSize];
                long total = 0;
                try
                {
                    while (true)
                    {
                        int count;
                        try
                        {
                            count = await source.ReadAsync(
                                    buffer,
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
                            throw new ModelDownloadException(
                                ModelDownloadError.NetworkFailure);
                        }
                        if (count == 0)
                        {
                            break;
                        }

                        total += count;
                        if (total > expectedBytes)
                        {
                            throw new ModelDownloadException(
                                ModelDownloadError.SizeExceeded);
                        }

                        try
                        {
                            await destination.WriteAsync(
                                    buffer.AsMemory(0, count),
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
                            throw new ModelDownloadException(
                                ModelDownloadError.CannotStore);
                        }
                        bytesReceived(total);
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (ModelDownloadException)
                {
                    throw;
                }
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsHttps(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(uri.Host);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
