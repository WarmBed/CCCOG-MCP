using System.Diagnostics;

namespace CCCG.Core;

public static class ProcessPassthrough
{
    public static async Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        Stream input,
        Stream output,
        Stream error,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "The configured Claude passthrough executable does not exist.",
                executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The configured Claude passthrough executable did not start.");
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                try
                {
                    ((Process)state!).Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The child already exited.
                }
            },
            process);

        var stdinTask = PumpInputAsync(input, process.StandardInput.BaseStream);
        var stdoutTask = PumpOutputAsync(process.StandardOutput.BaseStream, output);
        var stderrTask = PumpOutputAsync(process.StandardError.BaseStream, error);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
            // The child may close stdin before exiting.
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        if (stdinTask.IsCompleted)
        {
            await ObserveInputCompletionAsync(stdinTask).ConfigureAwait(false);
        }
        else
        {
            _ = stdinTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        return process.ExitCode;
    }

    private static async Task PumpInputAsync(Stream source, Stream destination)
    {
        try
        {
            await source.CopyToAsync(destination).ConfigureAwait(false);
            await destination.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
            // A child is allowed to close stdin as it exits.
        }
        finally
        {
            try
            {
                destination.Close();
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException)
            {
                // A child is allowed to close stdin as it exits.
            }
        }
    }

    private static async Task PumpOutputAsync(Stream source, Stream destination)
    {
        await source.CopyToAsync(destination).ConfigureAwait(false);
        await destination.FlushAsync().ConfigureAwait(false);
    }

    private static async Task ObserveInputCompletionAsync(Task inputTask)
    {
        try
        {
            await inputTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
            // A child is allowed to close stdin as it exits.
        }
    }
}
