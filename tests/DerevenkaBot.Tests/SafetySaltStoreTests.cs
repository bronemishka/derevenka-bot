using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Tests;

public sealed class SafetySaltStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "derevenka-tests", Guid.NewGuid().ToString("N"));
    private ImageOptions Options() => new() { Enabled = true, UseFakeProvider = false, ApiKey = "test-key", StoragePath = directory };

    [Fact]
    public void MissingSaltIsGeneratedAndReusedAfterRestart()
    {
        var first = Options(); first.Validate(); SafetySaltStore.Initialize(first);
        var second = Options(); SafetySaltStore.Initialize(second);
        Assert.Equal(32, Convert.FromBase64String(first.SafetySalt).Length);
        Assert.Equal(first.SafetySalt, second.SafetySalt);
    }
    [Fact]
    public async Task ConcurrentStartsUseTheSamePersistedSalt()
    {
        var instances = Enumerable.Range(0, 8).Select(_ => Options()).ToArray();
        await Task.WhenAll(instances.Select(o => Task.Run(() => SafetySaltStore.Initialize(o))));
        Assert.Single(instances.Select(o => o.SafetySalt).Distinct());
        Assert.Equal(instances[0].SafetySalt, File.ReadAllText(Path.Combine(directory, "safety-salt")));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact]
    public void ExplicitSaltIsPreservedWithoutCreatingStorage()
    {
        var options = Options(); options.SafetySalt = "custom-salt-at-least-16";
        SafetySaltStore.Initialize(options);
        Assert.Equal("custom-salt-at-least-16", options.SafetySalt);
        Assert.False(Directory.Exists(directory));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DisabledOrFakeModuleDoesNotCreateSalt(bool enabled, bool fake)
    {
        var options = Options(); options.Enabled = enabled; options.UseFakeProvider = fake;
        SafetySaltStore.Initialize(options);
        Assert.False(Directory.Exists(directory));
    }
    [Fact]
    public void CorruptPersistedSaltIsNotSilentlyReplaced()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "safety-salt");
        File.WriteAllText(path, "short");
        Assert.Throws<InvalidOperationException>(() => SafetySaltStore.Initialize(Options()));
        Assert.Equal("short", File.ReadAllText(path));
    }
    [Fact]
    public void ExplicitShortSaltHasSpecificValidationMessage()
    {
        var options = Options(); options.SafetySalt = "short";
        Assert.Contains("автоматической генерации", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
