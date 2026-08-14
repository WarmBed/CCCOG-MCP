using System.Text;

namespace CCCG.Core.Monitoring;

public sealed class IncrementalFileTailer
{
    private readonly string path;
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder pending = new();
    private long position;

    public IncrementalFileTailer(string path, bool startAtEnd)
    {
        this.path = path;
        position = startAtEnd && File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public IncrementalFileTailer(string path, long initialPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialPosition);
        this.path = path;
        position = initialPosition;
    }

    public IReadOnlyList<string> ReadNewLines()
    {
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < position)
        {
            position = 0;
            decoder.Reset();
            pending.Clear();
        }

        stream.Position = position;
        var byteCount = checked((int)(stream.Length - position));
        if (byteCount == 0)
        {
            return Array.Empty<string>();
        }

        var bytes = new byte[byteCount];
        var read = stream.Read(bytes, 0, bytes.Length);
        position += read;
        var chars = new char[Encoding.UTF8.GetMaxCharCount(read)];
        var charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
        pending.Append(chars, 0, charCount);

        var lines = new List<string>();
        while (true)
        {
            var newline = IndexOfNewline(pending);
            if (newline < 0)
            {
                break;
            }

            var length = newline > 0 && pending[newline - 1] == '\r' ? newline - 1 : newline;
            lines.Add(pending.ToString(0, length));
            pending.Remove(0, newline + 1);
        }

        return lines;
    }

    private static int IndexOfNewline(StringBuilder builder)
    {
        for (var index = 0; index < builder.Length; index++)
        {
            if (builder[index] == '\n')
            {
                return index;
            }
        }

        return -1;
    }
}
