using System.Diagnostics;

namespace Jularr.Web.Infrastructure;

public sealed record MediaProcessResult(
    int ExitCode,
    string Output,
    string Error)
{
    public string ErrorSummary
    {
        get
        {
            var trimmed = Error.Trim();
            return trimmed.Length <= 500 ? trimmed : trimmed[..500];
        }
    }
}

/// <summary>Runs an external media tool with a timeout; the seam that lets tests answer ffmpeg without starting it.</summary>
public interface IMediaProcessRunner
{
    /// <summary>Null when the tool could not start or exceeded <paramref name="timeout"/>; cancellation by the caller throws.</summary>
    Task<MediaProcessResult?> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class MediaProcessRunner(ILogger<MediaProcessRunner> logger) : IMediaProcessRunner
{
    public async Task<MediaProcessResult?> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "Could not start media tool {Executable}.", executable);
            return null;
        }

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return new MediaProcessResult(
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            logger.LogWarning(
                "Media tool {Executable} exceeded timeout {Timeout}.",
                executable,
                timeout);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
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
    }
}
