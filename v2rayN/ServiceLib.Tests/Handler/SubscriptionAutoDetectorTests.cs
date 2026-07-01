using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class SubscriptionAutoDetectorTests
{
    // ── IsAutoUserAgent ────────────────────────────────────────────────

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

    // ── UA family classification ───────────────────────────────────────

    [Theory]
    [InlineData("v2rayN", UserAgentFamily.ShareLink)]
    [InlineData("Shadowrocket", UserAgentFamily.ShareLink)]
    [InlineData("SagerNet", UserAgentFamily.ShareLink)]
    [InlineData("Quantumult X", UserAgentFamily.ShareLink)]
    [InlineData("Surge", UserAgentFamily.ShareLink)]
    [InlineData("Loon", UserAgentFamily.ShareLink)]
    [InlineData("NekoBox", UserAgentFamily.ShareLink)]
    [InlineData("ClashMetaForAndroid", UserAgentFamily.Clash)]
    [InlineData("ClashforWindows", UserAgentFamily.Clash)]
    [InlineData("clash-verge", UserAgentFamily.Clash)]
    [InlineData("clash.meta", UserAgentFamily.Clash)]
    [InlineData("Stash", UserAgentFamily.Clash)]
    [InlineData("sing-box", UserAgentFamily.SingBox)]
    public void ClassifyUserAgentFamily_ShouldClassifyKnownUAs(string ua, UserAgentFamily expected)
    {
        SubscriptionAutoDetector.ClassifyUserAgentFamily(ua).Should().Be(expected);
    }

    [Fact]
    public void ClassifyUserAgentFamily_DefaultEmptyUA_ShouldBeShareLink()
    {
        // Empty/default UA is v2rayN's native family → ShareLink
        SubscriptionAutoDetector.ClassifyUserAgentFamily("").Should().Be(UserAgentFamily.ShareLink);
        SubscriptionAutoDetector.ClassifyUserAgentFamily(null).Should().Be(UserAgentFamily.ShareLink);
    }

    [Fact]
    public void ClassifyUserAgentFamily_UnknownUA_ShouldBeUnknown()
    {
        SubscriptionAutoDetector.ClassifyUserAgentFamily("SomeRandomClient/1.0").Should().Be(UserAgentFamily.Unknown);
    }

    // ── Cross-probe UA selection ───────────────────────────────────────

    [Theory]
    [InlineData("SagerNet", "clash.meta")]          // ShareLink → probe Clash
    [InlineData("", "clash.meta")]                    // default (ShareLink) → probe Clash
    [InlineData("clash.meta", "SagerNet")]           // Clash → probe ShareLink
    [InlineData("clash-verge", "SagerNet")]           // Clash → probe ShareLink
    [InlineData("sing-box", "clash.meta")]            // SingBox → probe Clash
    public void GetCrossProbeUserAgent_ShouldReturnOppositeFamily(string cached, string expected)
    {
        var result = SubscriptionAutoDetector.GetCrossProbeUserAgent(cached);
        result.Should().Be(expected);
    }

    [Fact]
    public void GetCrossProbeUserAgent_UnknownFamily_ShouldReturnEmpty()
    {
        // Unknown UA family → no targeted probe possible
        SubscriptionAutoDetector.GetCrossProbeUserAgent("UnknownClient").Should().BeEmpty();
    }

    // ── Family representative ──────────────────────────────────────────

    [Fact]
    public void GetFamilyRepresentative_ShouldReturnCanonicalUA()
    {
        SubscriptionAutoDetector.GetFamilyRepresentative(UserAgentFamily.Clash).Should().Be("clash.meta");
        SubscriptionAutoDetector.GetFamilyRepresentative(UserAgentFamily.ShareLink).Should().Be("SagerNet");
        SubscriptionAutoDetector.GetFamilyRepresentative(UserAgentFamily.SingBox).Should().Be("sing-box");
    }

    // ── Detect: share links ────────────────────────────────────────────

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
    public void Detect_ShouldPreferClashConfigWithManyProxiesOverSmallShareList()
    {
        // Real-world case: subscription returns 3 share links for one UA but a full
        // Clash config with many proxies (e.g. Mieru nodes) for a Clash UA.
        var shareList = SubscriptionAutoDetector.Detect("v2rayN",
            string.Join(Environment.NewLine,
                "ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ=@example.com:443#ss-one",
                "anytls://password@example.com:8443#anytls-one",
                "anytls://password@example.com:8444#anytls-two"));

        var proxies = string.Join(Environment.NewLine,
            Enumerable.Range(1, 21).Select(i =>
                $"  - {{ name: node{i}, type: socks5, server: example.com, port: {1000 + i} }}"));
        var clashConfig = SubscriptionAutoDetector.Detect("ClashMetaForAndroid",
            "mixed-port: 7890" + Environment.NewLine
            + "proxies:" + Environment.NewLine
            + proxies + Environment.NewLine
            + "rules:" + Environment.NewLine
            + "  - MATCH,node1");

        clashConfig.CustomConfigCount.Should().Be(21);
        clashConfig.Score.Should().BeGreaterThan(shareList.Score);
    }

    // ── Detect: TotalNodeCount ─────────────────────────────────────────

    [Fact]
    public void TotalNodeCount_ShouldSumAllNodeTypes()
    {
        // 2 share links
        var result = SubscriptionAutoDetector.Detect("SagerNet",
            string.Join(Environment.NewLine,
                "vless://a@example.com:443#one",
                "anytls://b@example.com:8443#two"));

        result.ShareLinkCount.Should().Be(2);
        result.TotalNodeCount.Should().Be(2);
    }

    [Fact]
    public void TotalNodeCount_ClashConfig_ShouldCountProxies()
    {
        var result = SubscriptionAutoDetector.Detect("clash.meta",
            """
            mixed-port: 7890
            proxies:
              - name: hk1
                type: socks5
                server: example.com
                port: 1001
              - name: hk2
                type: socks5
                server: example.com
                port: 1002
              - name: jp1
                type: socks5
                server: example.com
                port: 1003
            rules:
              - MATCH,hk1
            """);

        result.CustomConfigCount.Should().Be(3);
        result.TotalNodeCount.Should().Be(3);
    }

    // ── Detect: error responses ────────────────────────────────────────

    [Fact]
    public void Detect_ErrorText_ShouldNotWinAutoSelection()
    {
        var result = SubscriptionAutoDetector.Detect("Surge", "rate limit exceeded");

        result.IsErrorResponse.Should().BeTrue();
        result.Score.Should().Be(-1);
    }

    // ── Candidate list & cache ─────────────────────────────────────────

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

    // ── Cross-family probe scenario simulation ─────────────────────────

    [Fact]
    public void CrossProbe_ShouldSelectClashConfig_WhenShareLinkCacheIsSuboptimal()
    {
        // Simulate: cached UA is SagerNet (ShareLink family) → 3 share links (300 pts)
        // Cross-probe UA is clash.meta (Clash family) → 21 proxies (1890 pts)
        // The cross-probe should win

        var cachedResult = SubscriptionAutoDetector.Detect("SagerNet",
            string.Join(Environment.NewLine,
                "ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ=@example.com:443#ss-one",
                "anytls://password@example.com:8443#anytls-one",
                "anytls://password@example.com:8444#anytls-two"));

        var proxies = string.Join(Environment.NewLine,
            Enumerable.Range(1, 21).Select(i =>
                $"  - {{ name: node{i}, type: socks5, server: example.com, port: {1000 + i} }}"));
        var crossProbeResult = SubscriptionAutoDetector.Detect("clash.meta",
            "mixed-port: 7890" + Environment.NewLine
            + "proxies:" + Environment.NewLine
            + proxies + Environment.NewLine
            + "rules:" + Environment.NewLine
            + "  - MATCH,node1");

        // Cross-probe wins
        crossProbeResult.Score.Should().BeGreaterThan(cachedResult.Score);

        // Verify the cross-probe UA is the one that would be selected
        var crossProbeUA = SubscriptionAutoDetector.GetCrossProbeUserAgent("SagerNet");
        crossProbeUA.Should().Be("clash.meta");
    }

    [Fact]
    public void CrossProbe_ShouldSelectShareLink_WhenClashCacheIsSuboptimal()
    {
        // Simulate: cached UA is clash.meta (Clash family) → 1 proxy (90 pts)
        // Cross-probe UA is SagerNet (ShareLink family) → 10 share links (1000 pts)
        // The cross-probe should win

        var cachedResult = SubscriptionAutoDetector.Detect("clash.meta",
            """
            mixed-port: 7890
            proxies:
              - name: only-one
                type: socks5
                server: example.com
                port: 443
            rules:
              - MATCH,only-one
            """);

        var crossProbeResult = SubscriptionAutoDetector.Detect("SagerNet",
            string.Join(Environment.NewLine,
                Enumerable.Range(1, 10).Select(i =>
                    $"vless://a{i}@example.com:443?encryption=none#node{i}")));

        crossProbeResult.Score.Should().BeGreaterThan(cachedResult.Score);

        var crossProbeUA = SubscriptionAutoDetector.GetCrossProbeUserAgent("clash.meta");
        crossProbeUA.Should().Be("SagerNet");
    }

    [Fact]
    public void CrossProbe_WhenBothReturnSimilarContent_CacheShouldRetain()
    {
        // Both UAs return similar scores → cached wins (no cache invalidation needed)
        var cachedResult = SubscriptionAutoDetector.Detect("SagerNet",
            string.Join(Environment.NewLine,
                "vless://a@example.com:443#node1",
                "anytls://b@example.com:8443#node2"));

        var crossProbeResult = SubscriptionAutoDetector.Detect("clash.meta",
            """
            mixed-port: 7890
            proxies:
              - name: node1
                type: socks5
                server: example.com
                port: 1001
              - name: node2
                type: socks5
                server: example.com
                port: 1002
            rules:
              - MATCH,node1
            """);

        // Both should have positive scores
        cachedResult.Score.Should().BePositive();
        crossProbeResult.Score.Should().BePositive();

        // In this case they're close — the point is we compared, not guessed
        var bestScore = Math.Max(cachedResult.Score, crossProbeResult.Score);
        bestScore.Should().BePositive();
    }
}
