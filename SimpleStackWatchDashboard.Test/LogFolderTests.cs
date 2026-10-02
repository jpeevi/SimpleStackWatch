using MonitorDashboard.Models;
using MonitorDashboard.Services;
using Xunit;

public sealed class LogFolderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "stackwatch-logs-" + Guid.NewGuid().ToString("N"));

    public LogFolderTests() => Directory.CreateDirectory(root);

    [Fact]
    public void EmptyListUsesOriginalFolderSetting()
    {
        var options = new LogOptions { Folder = root };
        FolderLogViewers.Configure(options, root);
        var folder = Assert.Single(options.Folders);
        Assert.Equal(root, folder.Folder);
        Assert.Equal("/integrations/file-logs/default/", folder.Url);
    }

    [Fact]
    public void FoldersKeepDistinctRoutesAndResolveRelativePaths()
    {
        Directory.CreateDirectory(Path.Combine(root, "one"));
        Directory.CreateDirectory(Path.Combine(root, "two"));
        var options = new LogOptions { Folders = [
            new() { Id = "one", Folder = "one" },
            new() { Id = "two", Name = "Second folder", Folder = "two" }
        ] };
        FolderLogViewers.Configure(options, root);
        Assert.Equal(Path.Combine(root, "one"), options.Folders[0].Folder);
        Assert.Equal("one", options.Folders[0].Name);
        Assert.NotEqual(options.Folders[0].Url, options.Folders[1].Url);
        Assert.Equal("Second folder", options.Folders[1].Name);
    }

    [Theory]
    [InlineData("../one")]
    [InlineData("one/two")]
    [InlineData("Nginx")]
    [InlineData("")]
    public void InvalidRouteIdsAreRejected(string id)
    {
        var options = new LogOptions { Folders = [new() { Id = id, Folder = root }] };
        Assert.Throws<InvalidOperationException>(() => FolderLogViewers.Configure(options, root));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        var options = new LogOptions { Folders = [
            new() { Id = "one", Folder = root }, new() { Id = "one", Folder = root }
        ] };
        Assert.Throws<InvalidOperationException>(() => FolderLogViewers.Configure(options, root));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
