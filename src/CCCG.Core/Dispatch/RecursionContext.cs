using System.Diagnostics;
using System.Globalization;

namespace CCCG.Core.Dispatch;

/// <summary>
/// The process-local recursion contract.  A worker process reads one completed
/// provider-edge count from its environment; creating a job adds exactly one
/// edge.  Host/worker infrastructure passes the value through unchanged.
/// </summary>
public sealed class RecursionContext
{
    public const string HopVariable = "CCCG_HOP";
    public const string MaxHopVariable = "CCCG_MAX_HOP";
    public const string SourceVariable = "CCCG_HOP_SOURCE";
    public const string ChainVariable = "CCCG_HOP_CHAIN";

    public RecursionContext(
        int processHop = 0,
        int maxHop = 2,
        string? hopSource = null,
        string? hopChain = null,
        string? quotaRoot = null)
    {
        if (processHop < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processHop));
        }

        if (maxHop <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHop));
        }

        ProcessHop = processHop;
        MaxHop = maxHop;
        HopSource = string.IsNullOrWhiteSpace(hopSource)
            ? DefaultSource()
            : hopSource.Trim();
        HopChain = string.IsNullOrWhiteSpace(hopChain)
            ? "human"
            : hopChain.Trim();
        QuotaRoot = quotaRoot;
    }

    public int ProcessHop { get; }

    public int MaxHop { get; }

    public int JobHop => checked(ProcessHop + 1);

    public string HopSource { get; }

    public string HopChain { get; }

    public string? QuotaRoot { get; }

    public bool IsRecursive => ProcessHop >= 1;

    public static RecursionContext FromEnvironment(
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var processHop = ParseNonNegative(getEnvironmentVariable(HopVariable), HopVariable, 0);
        var maxHop = ParsePositive(getEnvironmentVariable(MaxHopVariable), MaxHopVariable, 2);
        var source = getEnvironmentVariable(SourceVariable);
        var chain = getEnvironmentVariable(ChainVariable);
        return new RecursionContext(processHop, maxHop, source, chain);
    }

    public void ValidateNewJob()
    {
        if (JobHop > MaxHop)
        {
            throw new InvalidOperationException(
                "CCCG recursion guard rejected dispatch: hopCount=" + JobHop
                + " exceeds maxHop=" + MaxHop
                + "; source=" + HopSource
                + "; chain=" + HopChain
                + ". No provider process was started.");
        }
    }

    public IReadOnlyDictionary<string, string> ProviderEnvironment(string provider) =>
        EnvironmentForHop(JobHop, HopSource, HopChain, provider);

    public static IReadOnlyDictionary<string, string> EnvironmentForHop(
        int hop,
        string source,
        string chain,
        string provider)
    {
        if (hop < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hop));
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [HopVariable] = hop.ToString(CultureInfo.InvariantCulture),
            [SourceVariable] = source,
            [ChainVariable] = AppendChain(chain, provider)
        };
    }

    public static void ApplyInheritedEnvironment(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (var name in new[] { HopVariable, SourceVariable, ChainVariable, MaxHopVariable })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                startInfo.Environment[name] = value;
            }
        }
    }

    public static void ApplyEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }
    }

    public static string AppendChain(string chain, string provider)
    {
        var suffix = string.IsNullOrWhiteSpace(provider)
            ? "provider"
            : provider.Trim().ToLowerInvariant();
        var value = string.IsNullOrWhiteSpace(chain) ? "human" : chain.Trim();
        var appended = value + ">" + suffix;
        return appended.Length <= 512 ? appended : appended[..512];
    }

    private static int ParseNonNegative(string? value, string variable, int fallback)
    {
        if (value is null || value.Length == 0)
        {
            return fallback;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            throw new InvalidOperationException(
                variable + " must be a non-negative integer.");
        }

        return parsed;
    }

    private static int ParsePositive(string? value, string variable, int fallback)
    {
        if (value is null || value.Length == 0)
        {
            return fallback;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new InvalidOperationException(
                variable + " must be a positive integer.");
        }

        return parsed;
    }

    private static string DefaultSource()
    {
        var user = Environment.UserName;
        var machine = Environment.MachineName;
        return "user:" + (string.IsNullOrWhiteSpace(user) ? "unknown" : user)
            + "@" + (string.IsNullOrWhiteSpace(machine) ? "unknown" : machine);
    }
}
