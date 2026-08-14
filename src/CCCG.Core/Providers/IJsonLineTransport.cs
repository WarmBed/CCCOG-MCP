namespace CCCG.Core.Providers;

public interface IJsonLineTransport : IAsyncDisposable
{
    ValueTask WriteLineAsync(
        string line,
        CancellationToken cancellationToken = default);

    ValueTask<string?> ReadLineAsync(
        CancellationToken cancellationToken = default);
}
