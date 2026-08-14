using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCCG.Core;

public sealed record RouteTarget
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; init; }

    public string NormalizedProvider => Provider.Trim().ToLowerInvariant();
}

public sealed record RouterConfiguration
{
    private static readonly HashSet<string> SupportedProviders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "mock",
            "codex-app-server",
            "xai-responses"
        };

    [JsonPropertyName("unmappedBehavior")]
    public string UnmappedBehavior { get; init; } = "reject";

    [JsonPropertyName("originalClaudePath")]
    public string? OriginalClaudePath { get; init; }

    [JsonPropertyName("routes")]
    public Dictionary<string, RouteTarget> Routes { get; init; } =
        new(StringComparer.Ordinal);

    public static RouterConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var json = File.ReadAllText(path);
        var configuration = JsonSerializer.Deserialize<RouterConfiguration>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException("Route configuration is empty.");

        if (!string.IsNullOrWhiteSpace(configuration.OriginalClaudePath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(
                configuration.OriginalClaudePath);
            if (!Path.IsPathRooted(expanded))
            {
                var configurationDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(path))!;
                expanded = Path.Combine(configurationDirectory, expanded);
            }

            configuration = configuration with
            {
                OriginalClaudePath = Path.GetFullPath(expanded)
            };
        }

        configuration.Validate();
        return configuration;
    }

    public RouteTarget Resolve(string claudeModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claudeModelId);

        return Routes.TryGetValue(claudeModelId, out var route)
            ? route
            : throw new KeyNotFoundException(
                $"No CCCG route is configured for Claude model '{claudeModelId}'.");
    }

    public bool TryResolve(string claudeModelId, out RouteTarget? route) =>
        Routes.TryGetValue(claudeModelId, out route);

    public bool UsesPassthrough => string.Equals(
        UnmappedBehavior,
        "passthrough",
        StringComparison.OrdinalIgnoreCase);

    private void Validate()
    {
        if (!string.Equals(UnmappedBehavior, "reject", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(UnmappedBehavior, "passthrough", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "unmappedBehavior must be either 'reject' or 'passthrough'.");
        }

        if (string.Equals(UnmappedBehavior, "passthrough", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(OriginalClaudePath))
        {
            throw new InvalidDataException(
                "originalClaudePath is required when unmappedBehavior is 'passthrough'.");
        }

        foreach (var (claudeModel, route) in Routes)
        {
            if (string.IsNullOrWhiteSpace(claudeModel))
            {
                throw new InvalidDataException("Route keys cannot be empty.");
            }

            if (!SupportedProviders.Contains(route.Provider))
            {
                throw new InvalidDataException(
                    $"Route '{claudeModel}' uses unsupported provider '{route.Provider}'.");
            }

            if (string.IsNullOrWhiteSpace(route.Model))
            {
                throw new InvalidDataException(
                    $"Route '{claudeModel}' must specify a backend model.");
            }
        }
    }
}
