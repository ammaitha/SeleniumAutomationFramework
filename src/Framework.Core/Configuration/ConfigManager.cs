using Microsoft.Extensions.Configuration;

namespace Framework.Core.Configuration;

/// <summary>
/// Thin static wrapper around <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
/// Loads settings from <c>appsettings.json</c> and environment variables, then resolves the
/// active test environment from a single <c>TestSettings:Environments</c> section.
/// Exposes the final environment-specific <c>TestSettings:Environment</c>,
/// <c>TestSettings:BaseUrl</c>, and <c>TestSettings:AppUrl</c> values via in-memory overrides.
/// Provides typed accessors — <see cref="GetString"/>, <see cref="GetInt"/>,
/// <see cref="GetBool"/> — that enforce required configuration keys with validation.
/// Throws <see cref="InvalidOperationException"/> if a required key is missing or invalid.
/// </summary>
public static class ConfigManager
{
    private static readonly Lazy<IConfigurationRoot> ConfigRoot = new(() => LoadConfiguration());

    public static string GetString(string key)
    {
        var value = ConfigRoot.Value[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required config key: {key}");
        return value;
    }

    public static int GetInt(string key)
    {
        var value = ConfigRoot.Value[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required config key: {key}");

        if (int.TryParse(value, out var parsed))
            return parsed;

        throw new InvalidOperationException($"Config key '{key}' has invalid int value: {value}");
    }

    public static bool GetBool(string key)
    {
        var value = ConfigRoot.Value[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required config key: {key}");

        if (bool.TryParse(value, out var parsed))
            return parsed;

        throw new InvalidOperationException($"Config key '{key}' has invalid bool value: {value}");
    }

    public static IConfigurationRoot LoadConfiguration(string? env = null, string? baseUrl = null, string? appUrl = null)
    {
        var baseConfiguration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        // Resolve the active environment from method override, then TEST_ENV, then configured default.
        var defaultEnvironment = baseConfiguration["TestSettings:DefaultEnvironment"] ?? "local";
        var resolvedEnvironment = env
            ?? baseConfiguration["TEST_ENV"]
            ?? defaultEnvironment;

        var environmentSection = baseConfiguration.GetSection($"TestSettings:Environments:{resolvedEnvironment}");
        if (!environmentSection.Exists())
            throw new InvalidOperationException(
                $"Configuration for environment '{resolvedEnvironment}' was not found under 'TestSettings:Environments'.");

        // Resolve URLs from method overrides first, then environment variables, then the selected environment block.
        var resolvedBaseUrl = baseUrl
            ?? baseConfiguration["TestSettings:BaseUrl"]
            ?? environmentSection["BaseUrl"];

        var resolvedAppUrl = appUrl
            ?? baseConfiguration["TestSettings:AppUrl"]
            ?? environmentSection["AppUrl"];

        if (string.IsNullOrWhiteSpace(resolvedBaseUrl))
            throw new InvalidOperationException(
                $"BaseUrl is not configured for environment '{resolvedEnvironment}'. Provide 'TestSettings:Environments:{resolvedEnvironment}:BaseUrl' or an override.");

        if (string.IsNullOrWhiteSpace(resolvedAppUrl))
            throw new InvalidOperationException(
                $"AppUrl is not configured for environment '{resolvedEnvironment}'. Provide 'TestSettings:Environments:{resolvedEnvironment}:AppUrl' or an override.");

        var finalOverrides = new Dictionary<string, string?>
        {
            ["TestSettings:Environment"] = resolvedEnvironment,
            ["TestSettings:BaseUrl"] = resolvedBaseUrl,
            ["TestSettings:AppUrl"] = resolvedAppUrl
        };

        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(finalOverrides)
            .Build();
    }
}