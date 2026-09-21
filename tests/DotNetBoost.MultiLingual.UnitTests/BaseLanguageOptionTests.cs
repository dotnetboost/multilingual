using DotNetBoost.MultiLingual.Core;

namespace DotNetBoost.MultiLingual.UnitTests;

public sealed class BaseLanguageOptionTests
{
    [Fact]
    public void BaseLanguage_DefaultsToUnset()
    {
        var options = new MultiLingualOptions();

        Assert.Null(options.BaseLanguage);
        Assert.False(options.HasBaseLanguage);
    }

    /// <summary>Every base-language branch has to stay inert in the default mode.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    [InlineData(null)]
    public void IsBaseLanguage_IsAlwaysFalse_WhenUnset(string? code) =>
        Assert.False(new MultiLingualOptions().IsBaseLanguage(code));

    [Theory]
    [InlineData("EN", "en")]
    [InlineData(" en ", "en")]
    [InlineData("pt-br", "pt-BR")]
    public void BaseLanguage_IsCanonicalisedOnAssignment(string assigned, string expected)
    {
        var options = new MultiLingualOptions { BaseLanguage = assigned };

        Assert.Equal(expected, options.BaseLanguage);
        Assert.True(options.HasBaseLanguage);
    }

    [Fact]
    public void BaseLanguage_RejectsAnUnusableCode() =>
        Assert.Throws<InvalidLanguageException>(() => new MultiLingualOptions { BaseLanguage = "not-a-language" });

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BaseLanguage_TreatsBlankAsUnset(string blank) =>
        Assert.False(new MultiLingualOptions { BaseLanguage = blank }.HasBaseLanguage);

    [Fact]
    public void IsBaseLanguage_IgnoresCase()
    {
        var options = new MultiLingualOptions { BaseLanguage = "en" };

        Assert.True(options.IsBaseLanguage("EN"));
        Assert.True(options.IsBaseLanguage("en"));
        Assert.False(options.IsBaseLanguage("ar"));
        Assert.False(options.IsBaseLanguage(null));
    }
}
