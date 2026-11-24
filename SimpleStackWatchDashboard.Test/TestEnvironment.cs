using Microsoft.Extensions.Configuration;

// The test project alone may read its untracked testconfig.json.
// The production host continues accepting private settings only from environment variables.
internal sealed class TestEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);

    private TestEnvironment() { }

    public static TestEnvironment Load(string? path = null)
    {
        var file = Path.GetFullPath(path ?? Path.Combine(AppContext.BaseDirectory, "testconfig.json"));
        if (!File.Exists(file)) return new TestEnvironment();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(file)!)
            .AddJsonFile(Path.GetFileName(file), optional: true, reloadOnChange: false)
            .Build();

        var scope = new TestEnvironment();
        foreach (var entry in configuration.AsEnumerable())
        {
            if (string.IsNullOrWhiteSpace(entry.Value) ||
                !(entry.Key.StartsWith("Dashboard:", StringComparison.OrdinalIgnoreCase) ||
                  entry.Key.StartsWith("LogSettings:", StringComparison.OrdinalIgnoreCase)))
                continue;

            var key = entry.Key.Replace(":", "__", StringComparison.Ordinal);
            // A real environment setting always wins over the local JSON file.
            if (Environment.GetEnvironmentVariable(key) is null)
                scope.Set(key, entry.Value);
        }

        return scope;
    }

    // Fixtures force isolated storage and per-test credentials regardless of local settings.
    public void Set(string key, string? value)
    {
        if (!previous.ContainsKey(key))
            previous.Add(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, value) in previous)
            Environment.SetEnvironmentVariable(key, value);
    }
}
