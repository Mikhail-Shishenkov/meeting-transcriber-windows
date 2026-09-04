using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PolinMegatranscriber.Core;

internal sealed record ProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

internal sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputWasTruncated,
    bool StandardErrorWasTruncated);

internal enum ProcessRunnerError
{
    InvalidRequest,
    ExecutableUnavailable,
    LaunchFailed,
    StreamReadFailed,
    TimedOut,
}

internal sealed class ProcessRunnerException : Exception
{
    internal ProcessRunnerException(
        ProcessRunnerError error,
        Exception? innerException = null)
        : base(error.ToString(), innerException)
    {
        Error = error;
    }

    internal ProcessRunnerError Error { get; }
}

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Func<string, ValueTask>? standardOutputHandler = null,
        CancellationToken cancellationToken = default);
}

internal sealed class WindowsProcessRunner : IProcessRunner
{
    private const int MaximumDiagnosticLimit = 64 * 1024;
    private const int StreamBufferSize = 8 * 1024;

    private readonly int diagnosticLimit;

    internal WindowsProcessRunner(int diagnosticLimit = MaximumDiagnosticLimit)
    {
        if (diagnosticLimit is < 1 or > MaximumDiagnosticLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticLimit));
        }

        this.diagnosticLimit = diagnosticLimit;
    }

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Func<string, ValueTask>? standardOutputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request),
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start())
            {
                throw new ProcessRunnerException(ProcessRunnerError.LaunchFailed);
            }
        }
        catch (ProcessRunnerException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or IOException)
        {
            throw new ProcessRunnerException(
                ProcessRunnerError.LaunchFailed,
                exception);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException)
        {
            TryTerminate(process);
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            throw new ProcessRunnerException(
                ProcessRunnerError.LaunchFailed,
                exception);
        }

        var output = new BoundedTextBuffer(diagnosticLimit);
        var error = new BoundedTextBuffer(diagnosticLimit);
        Task outputDrain = DrainAsync(
            process.StandardOutput,
            output,
            standardOutputHandler,
            process);
        Task errorDrain = DrainAsync(
            process.StandardError,
            error,
            handler: null,
            process);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using CancellationTokenSource stop =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);

        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateAndDrainAsync(
                    process,
                    outputDrain,
                    errorDrain)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new ProcessRunnerException(ProcessRunnerError.TimedOut);
        }

        try
        {
            await Task.WhenAll(outputDrain, errorDrain).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TerminateAndDrainAsync(
                    process,
                    Task.CompletedTask,
                    Task.CompletedTask)
                .ConfigureAwait(false);
            throw new ProcessRunnerException(
                ProcessRunnerError.StreamReadFailed,
                exception);
        }

        return new ProcessResult(
            process.ExitCode,
            output.Text,
            error.Text,
            output.WasTruncated,
            error.WasTruncated);
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task DrainAsync(
        StreamReader reader,
        BoundedTextBuffer buffer,
        Func<string, ValueTask>? handler,
        Process process)
    {
        char[] characters = new char[StreamBufferSize];
        try
        {
            while (true)
            {
                int count = await reader.ReadAsync(characters).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }

                string chunk = new(characters, 0, count);
                buffer.Append(chunk);
                if (handler is not null)
                {
                    await handler(chunk).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryTerminate(process);
            throw;
        }
    }

    private static async Task TerminateAndDrainAsync(
        Process process,
        Task outputDrain,
        Task errorDrain)
    {
        TryTerminate(process);
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await Task.WhenAll(outputDrain, errorDrain).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static void ValidateRequest(ProcessRequest request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.ExecutablePath)
            || request.Arguments is null
            || request.Arguments.Any(argument => argument is null)
            || request.Timeout <= TimeSpan.Zero
            || request.Timeout > TimeSpan.FromDays(7))
        {
            throw new ProcessRunnerException(ProcessRunnerError.InvalidRequest);
        }

        try
        {
            var executable = new FileInfo(request.ExecutablePath);
            if (!executable.Exists
                || (executable.Attributes & FileAttributes.Directory) != 0)
            {
                throw new ProcessRunnerException(
                    ProcessRunnerError.ExecutableUnavailable);
            }
        }
        catch (ProcessRunnerException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            throw new ProcessRunnerException(
                ProcessRunnerError.ExecutableUnavailable,
                exception);
        }
    }

    private sealed class BoundedTextBuffer
    {
        private readonly int limit;
        private readonly StringBuilder builder = new();

        internal BoundedTextBuffer(int limit)
        {
            this.limit = limit;
        }

        internal string Text => builder.ToString();

        internal bool WasTruncated { get; private set; }

        internal void Append(string value)
        {
            int remaining = limit - builder.Length;
            if (remaining <= 0)
            {
                WasTruncated = true;
                return;
            }

            if (value.Length > remaining)
            {
                builder.Append(value.AsSpan(0, remaining));
                WasTruncated = true;
            }
            else
            {
                builder.Append(value);
            }
        }
    }
}
