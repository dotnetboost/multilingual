using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Metadata;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>Internal, not public: Build() validates exported types only, and this one is fine anyway.</summary>
internal sealed class Slugged
{
    public string Id { get; set; } = string.Empty;
    [MultiLingual] public string? Name { get; set; }
}

/// <summary>Keys too long to store.</summary>
public sealed class ClearAndKeyLimitTests
{
    // ================================================================== key limit

    [Fact]
    public async Task Save_KeyLongerThanTheColumn_IsRefused_WithTheEntityNamed()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Translations.SaveAsync(new Slugged { Id = new string('x', EntityKey.MaxLength + 1), Name = "n" }));

        Assert.Contains(nameof(Slugged), ex.Message, StringComparison.Ordinal);
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
        Assert.Empty(engine.Translations.All);
    }

    [Fact]
    public async Task Save_KeyOfExactlyTheMaximum_IsStored()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.Translations.SaveAsync(new Slugged { Id = new string('x', EntityKey.MaxLength), Name = "n" });

        Assert.Single(engine.Translations.All);
    }
}
