using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("Play again", "再播放一次", "再播放一次")]
    [InlineData("Did you hear it?", "听到声音了吗？", "聽到聲音了嗎？")]
    [InlineData("I heard it", "听到了", "聽到了")]
    [InlineData("No audio", "没听到", "沒聽到")]
    [InlineData("About AppleHome", "关于 AppleHome", "關於 AppleHome")]
    public void SetupLabelsHaveBothChineseTranslations(string english, string simplified, string traditional)
    {
        foreach (var (resource, expected) in new[] { ("zh", simplified), ("zh-TW", traditional) })
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"AirFlash.Core.Strings.{resource}.json")!;
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            Assert.True(translations.TryGetValue(english, out var actual), $"Missing {resource} setup translation: {english}");
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("ZH-hans", "zh")]
    [InlineData("en-GB", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("", "en")]
    public void SelectsSupportedLanguageOrEnglishFallback(string preference, string expected)
        => Assert.Equal(expected, L.SelectLanguage([preference]));

    [Fact]
    public void RespectsUserPreferenceOrder()
    {
        Assert.Equal("en", L.SelectLanguage(["en-US", "zh-CN"]));
        Assert.Equal("zh-TW", L.SelectLanguage(["fr-FR", "zh-TW", "en-US"]));
        Assert.Equal("zh", L.SelectLanguage(["fr-FR", "zh-CN", "en-US"]));
        Assert.Equal("en", L.SelectLanguage([]));
    }

    [Fact]
    public void EnglishWorksWithoutInitializationAndUnknownMessagesRemainIntact()
    {
        Assert.Equal("Settings…", L.Get("Settings…"));
        Assert.Equal("Unknown engine detail", L.Get("Unknown engine detail"));
        Assert.Equal("HomePod stereo · 2/2", L.Format("HomePod stereo · {0}/2", 2));
    }

    [Fact]
    public void EmbeddedChineseCatalogPreservesFormatArguments()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("AirFlash.Core.Strings.zh.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        Assert.Equal("设置…", translations["Settings…"]);
        foreach (var (english, chinese) in translations)
        {
            Assert.False(string.IsNullOrWhiteSpace(chinese));
            Assert.Equal(System.Text.CompositeFormat.Parse(english).MinimumArgumentCount,
                System.Text.CompositeFormat.Parse(chinese).MinimumArgumentCount);
        }
    }

    [Fact]
    public void EmbeddedTraditionalChineseCatalogPreservesFormatArguments()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("AirFlash.Core.Strings.zh-TW.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        Assert.Equal("設定…", translations["Settings…"]);
        foreach (var (english, traditional) in translations)
        {
            Assert.False(string.IsNullOrWhiteSpace(traditional));
            Assert.Equal(System.Text.CompositeFormat.Parse(english).MinimumArgumentCount,
                System.Text.CompositeFormat.Parse(traditional).MinimumArgumentCount);
        }
    }
}
