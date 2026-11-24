using Xunit;

public sealed class TestEnvironmentTests
{
    [Fact]
    public void TestJsonLoadsOnlyIntoTestEnvironmentAndPreservesExistingOverrides()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ssw-testconfig-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "testconfig.json");
        const string redisKey = "Dashboard__Redis__ConnectionString";
        const string mcpKey = "Dashboard__Mcp__Enabled";
        var priorRedis = Environment.GetEnvironmentVariable(redisKey);
        var priorMcp = Environment.GetEnvironmentVariable(mcpKey);
        var priorUnrelated = Environment.GetEnvironmentVariable("Unrelated__MustNotBeExported");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(path, """
                {
                  "Dashboard": {
                    "Redis": { "ConnectionString": "example.invalid:6379" },
                    "Mcp": { "Enabled": true }
                  },
                  "Unrelated": { "MustNotBeExported": "value" }
                }
                """);
            Environment.SetEnvironmentVariable(redisKey, null);
            Environment.SetEnvironmentVariable(mcpKey, "false");

            using (var scope = TestEnvironment.Load(path))
            {
                Assert.Equal("example.invalid:6379", Environment.GetEnvironmentVariable(redisKey));
                Assert.Equal("false", Environment.GetEnvironmentVariable(mcpKey));
                Assert.Equal(priorUnrelated, Environment.GetEnvironmentVariable("Unrelated__MustNotBeExported"));
                scope.Set(redisKey, "fixture-override");
                Assert.Equal("fixture-override", Environment.GetEnvironmentVariable(redisKey));
            }

            Assert.Null(Environment.GetEnvironmentVariable(redisKey));
            Assert.Equal("false", Environment.GetEnvironmentVariable(mcpKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(redisKey, priorRedis);
            Environment.SetEnvironmentVariable(mcpKey, priorMcp);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingTestConfigIsOptional()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ssw-absent-" + Guid.NewGuid().ToString("N"), "testconfig.json");
        using var scope = TestEnvironment.Load(missing);
    }
}
