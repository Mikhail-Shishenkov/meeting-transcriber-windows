using System.Buffers;
using System.Security.Cryptography;

namespace PolinMegatranscriber.Core;

internal interface IModelFileVerifier
{
    Task VerifyAsync(
        string path,
        ModelDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

internal sealed class StreamingModelFileVerifier : IModelFileVerifier
{
    private const int BufferSize = 1024 * 1024;

    public async Task VerifyAsync(
        string path,
        ModelDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists
                || (file.Attributes
                    & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                    != 0
                || file.Length != descriptor.SizeBytes)
            {
                throw new ModelVerificationException();
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long total = 0;
            try
            {
                while (true)
                {
                    int count = await stream.ReadAsync(
                            buffer.AsMemory(0, BufferSize),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    total += count;
                    if (total > descriptor.SizeBytes)
                    {
                        throw new ModelVerificationException();
                    }

                    hash.AppendData(buffer, 0, count);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            string actual = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            if (total != descriptor.SizeBytes
                || !string.Equals(
                    actual,
                    descriptor.Sha256,
                    StringComparison.Ordinal))
            {
                throw new ModelVerificationException();
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelVerificationException)
        {
            throw;
        }
        catch
        {
            throw new ModelVerificationException();
        }
    }
}

internal sealed class ModelVerificationException : Exception;
