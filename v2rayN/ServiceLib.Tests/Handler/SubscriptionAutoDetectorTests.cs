using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class SubscriptionAutoDetectorTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Auto", true)]
    [InlineData("auto", true)]
    [InlineData("Shadowrocket", false)]
    public void IsAutoUserAgent_ShouldTreatEmptyAndAutoAsAuto(string? userAgent, bool expected)
    {
        SubscriptionAutoDetector.IsAutoUserAgent(userAgent).Should().Be(expected);
    }

    [Fact]
    public void Detect_Base64ShareLinks_ShouldCountSupportedLinks()
    {
        var content = Utils.Base64Encode(string.Join(Environment.NewLine,
            "vless://ddb490a1-126f-5e88-955e-0fe32d634e37@example.com:443?encryption=none#vless-one",
            "anytls://password@example.com:8443#anytls-one",
            "unsupported://password@example.com:8443#ignored"));

        var result = SubscriptionAutoDetector.Detect("Shadowrocket", content);

        result.ShareLinkCount.Should().Be(2);
        result.Score.Should().Be(200);
        result.IsErrorResponse.Should().BeFalse();
    }

    [Fact]
    public void Detect_WithFilter_ShouldScoreImportableLinksAfterFiltering()
    {
        var content = string.Join(Environment.NewLine,
            "vless://ddb490a1-126f-5e88-955e-0fe32d634e37@example.com:443?encryption=none#HongKong",
            "anytls://password@example.com:8443#Japan");

        var result = SubscriptionAutoDetector.Detect("Shadowrocket", content, "Japan");

        result.ShareLinkCount.Should().Be(1);
        result.Score.Should().Be(100);
    }

    [Fact]
    public void Detect_ShouldPreferShareListsOverWholeCustomConfig()
    {
        var shareList = SubscriptionAutoDetector.Detect("Shadowrocket",
            "anytls://password@example.com:8443#anytls-one");
        var clashConfig = SubscriptionAutoDetector.Detect("ClashMetaForAndroid",
            """
            mixed-port: 7890
            proxies:
              - name: demo
                type: socks5
                server: example.com
                port: 443
            rules:
              - MATCH,demo
            """);

        shareList.Score.Should().BeGreaterThan(clashConfig.Score);
        clashConfig.CustomConfigCount.Should().Be(1);
    }

    [Fact]
    public void Detect_ErrorText_ShouldNotWinAutoSelection()
    {
        var result = SubscriptionAutoDetector.Detect("Surge", "rate limit exceeded");

        result.IsErrorResponse.Should().BeTrue();
        result.Score.Should().Be(-1);
    }

    [Fact]
    public void GetCandidateUserAgents_ShouldPutCachedUserAgentFirst()
    {
        var url = $"https://example.com/{Guid.NewGuid():N}";
        SubscriptionAutoDetector.SavePreferredUserAgent(url, "Shadowrocket", "Japan");

        var candidates = SubscriptionAutoDetector.GetCandidateUserAgents(url, "Japan").ToList();

        candidates.First().Should().Be("Shadowrocket");
        candidates.Should().Contain(string.Empty);
        candidates.Distinct().Should().HaveCount(candidates.Count);
    }

    [Fact]
    public void GetCandidateUserAgents_ShouldSeparateCacheByFilter()
    {
        var url = $"https://example.com/{Guid.NewGuid():N}";
        SubscriptionAutoDetector.SavePreferredUserAgent(url, "Shadowrocket", "Japan");
        SubscriptionAutoDetector.SavePreferredUserAgent(url, "Surge", "HongKong");

        SubscriptionAutoDetector.GetCandidateUserAgents(url, "Japan").First().Should().Be("Shadowrocket");
        SubscriptionAutoDetector.GetCandidateUserAgents(url, "HongKong").First().Should().Be("Surge");
    }

    [Fact]
    public void PersistedUserAgent_ShouldRoundTripDefaultUserAgent()
    {
        var persisted = SubscriptionAutoDetector.ToPersistedUserAgent(string.Empty);

        persisted.Should().NotBeEmpty();
        SubscriptionAutoDetector.FromPersistedUserAgent(persisted).Should().BeEmpty();
    }
}
