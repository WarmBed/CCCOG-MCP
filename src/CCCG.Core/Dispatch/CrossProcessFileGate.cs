using System.Security.Cryptography;
using System.Text;

namespace CCCG.Core.Dispatch;

public sealed class CrossProcessFileGate : IDisposable
{
    private readonly FileStream stream;

    private CrossProcessFileGate(FileStream stream)
    {
        this.stream = stream;
    }

    public static CrossProcessFileGate Acquire(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var deadline = DateTime.UtcNow + timeout;
        IOException? lastError = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new CrossProcessFileGate(new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough));
            }
            catch (IOException exception)
            {
                lastError = exception;
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for the CCCG cross-process gate '{path}'.",
                        lastError);
                }

                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(25));
            }
        }
    }

    public static string Key(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static void AtomicWriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose() => stream.Dispose();
}
