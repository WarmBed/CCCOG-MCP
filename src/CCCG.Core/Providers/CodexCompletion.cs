namespace CCCG.Core.Providers;

public sealed record CodexCompletion(
    string Text,
    string RequestedModel,
    string ActualModel,
    bool WasRerouted,
    string? RerouteReason = null);

public static class ProviderDisclosure
{
    public static string Format(
        string provider,
        string actualModel,
        string text,
        string? requestedModel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualModel);

        var reroute = !string.IsNullOrWhiteSpace(requestedModel)
            && !string.Equals(requestedModel, actualModel, StringComparison.Ordinal)
                ? $" rerouted-from={requestedModel}"
                : string.Empty;

        return $"[CCCG provider={provider} model={actualModel}{reroute}]\n{text}";
    }
}
