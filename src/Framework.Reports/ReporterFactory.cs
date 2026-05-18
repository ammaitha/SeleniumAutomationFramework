namespace Framework.Reports;

public static class ReporterFactory
{
    private static readonly Lazy<Dictionary<string, Type>> Registry = new(DiscoverReporters);

    public static IReadOnlyCollection<string> SupportedReporters => Registry.Value.Keys.ToArray();

    public static bool TryCreate(string? name, out IReporter? reporter)
    {
        reporter = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!Registry.Value.TryGetValue(name.Trim(), out var reporterType))
        {
            return false;
        }

        reporter = (IReporter)Activator.CreateInstance(reporterType)!;
        return true;
    }

    public static bool TryCreateFirstAvailable(out IReporter? reporter, out string? reporterName)
    {
        reporter = null;
        reporterName = null;

        foreach (var entry in Registry.Value)
        {
            reporter = (IReporter)Activator.CreateInstance(entry.Value)!;
            reporterName = entry.Key;
            return true;
        }

        return false;
    }

    public static IReporter Create(string name)
    {
        if (TryCreate(name, out var reporter))
        {
            return reporter!;
        }

        throw new InvalidOperationException(
            $"Unknown reporter '{name}'. Supported values: {string.Join(", ", SupportedReporters)}.");
    }

    private static Dictionary<string, Type> DiscoverReporters()
    {
        var registry = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var reporterTypes = typeof(ReporterFactory).Assembly
            .GetTypes()
            .Where(type => typeof(IReporter).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            .ToList();

        foreach (var reporterType in reporterTypes)
        {
            var aliases = reporterType
                .GetCustomAttributes(typeof(ReporterAliasAttribute), inherit: false)
                .Cast<ReporterAliasAttribute>()
                .Select(attribute => attribute.Name)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .ToList();

            if (aliases.Count == 0 && reporterType.Name.EndsWith("Reporter", StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add(reporterType.Name[..^"Reporter".Length]);
            }

            foreach (var alias in aliases)
            {
                if (!registry.ContainsKey(alias))
                {
                    registry[alias] = reporterType;
                }
            }
        }

        return registry;
    }
}
