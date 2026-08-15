namespace CCCG.Core.Dispatch;

public sealed record TranscriptReadRequest(
    string Provider,
    string SessionId,
    int Limit = 20,
    string? BeforeMarker = null);

public sealed record TranscriptTurn(
    string Role,
    string Text,
    int RecordIndex);

public sealed record TranscriptReadResult(
    string Provider,
    string SessionId,
    string? Title,
    DateTimeOffset? UpdatedAt,
    string Rendered,
    int ReturnedRounds,
    bool HasMore,
    string? BeforeMarker,
    bool Truncated,
    int SkippedRecords,
    string Warning);

public sealed class TranscriptReaderOptions
{
    public int MaxFileBytes { get; init; } = 2 * 1024 * 1024;

    public int MaxOutputBytes { get; init; } = 50 * 1024;

    public int TailBytes { get; init; } = 256 * 1024;
}
