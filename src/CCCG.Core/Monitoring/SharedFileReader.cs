using System.Text;

namespace CCCG.Core.Monitoring;

public static class SharedFileReader
{
    public static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
