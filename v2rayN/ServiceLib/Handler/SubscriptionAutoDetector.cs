namespace ServiceLib.Handler;

public sealed record SubscriptionDetectResult(
    string UserAgent,
    string Content,
    int Score,
    int ShareLinkCount,
    int InnerLinkCount,
    int CustomConfigCount,
    bool IsErrorResponse);

public static class SubscriptionAutoDetector
{
    private const string DefaultUserAgentCacheValue = "<default>";

    private static readonly ConcurrentDictionary<string, string> _preferredUserAgentCache = new();

    public static bool IsAutoUserAgent(string? userAgent)
    {
        return userAgent.IsNullOrEmpty()
               || userAgent.Equals(Global.SubscriptionUserAgentAuto, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> GetCandidateUserAgents(string url, string? filter = null)
    {
        var yielded = new HashSet<string>();

        if (_preferredUserAgentCache.TryGetValue(GetCacheKey(url, filter), out var cachedUserAgent)
            && yielded.Add(FromCacheValue(cachedUserAgent)))
        {
            yield return FromCacheValue(cachedUserAgent);
        }

        foreach (var userAgent in Global.SubscriptionUserAgents)
        {
            if (userAgent == Global.SubscriptionUserAgentAuto)
            {
                if (yielded.Add(string.Empty))
                {
                    yield return string.Empty;
                }
                continue;
            }

            if (yielded.Add(userAgent))
            {
                yield return userAgent;
            }
        }
    }

    public static bool TryGetPreferredUserAgent(string url, string? filter, out string userAgent)
    {
        if (_preferredUserAgentCache.TryGetValue(GetCacheKey(url, filter), out var cachedUserAgent))
        {
            userAgent = FromCacheValue(cachedUserAgent);
            return true;
        }

        userAgent = string.Empty;
        return false;
    }

    public static SubscriptionDetectResult Detect(string userAgent, string content, string? filter = null)
    {
        var normalizedContent = DecodeIfBase64(content);
        var shareLinkCount = CountShareLinks(normalizedContent, filter);
        var innerLinkCount = CountInnerLinks(normalizedContent);
        var customConfigCount = CountCustomConfigs(normalizedContent);
        var isErrorResponse = IsLikelyErrorResponse(normalizedContent);
        var score = isErrorResponse ? -1 : shareLinkCount * 100 + innerLinkCount * 90 + customConfigCount;

        return new SubscriptionDetectResult(
            userAgent,
            content,
            score,
            shareLinkCount,
            innerLinkCount,
            customConfigCount,
            isErrorResponse);
    }

    public static void SavePreferredUserAgent(string url, string userAgent, string? filter = null)
    {
        if (url.IsNullOrEmpty())
        {
            return;
        }

        _preferredUserAgentCache[GetCacheKey(url, filter)] = ToCacheValue(userAgent);
    }

    public static string ToPersistedUserAgent(string userAgent)
    {
        return ToCacheValue(userAgent);
    }

    public static string FromPersistedUserAgent(string userAgent)
    {
        return FromCacheValue(userAgent);
    }

    private static string ToCacheValue(string userAgent)
    {
        return userAgent.IsNullOrEmpty()
            ? DefaultUserAgentCacheValue
            : userAgent;
    }

    private static string GetCacheKey(string url, string? filter)
    {
        return $"{url}\n{filter.TrimEx()}";
    }

    private static string FromCacheValue(string userAgent)
    {
        return userAgent == DefaultUserAgentCacheValue
            ? string.Empty
            : userAgent;
    }

    private static string DecodeIfBase64(string content)
    {
        if (content.IsNullOrEmpty())
        {
            return string.Empty;
        }

        return Utils.IsBase64String(content)
            ? Utils.Base64Decode(content)
            : content;
    }

    private static int CountShareLinks(string content, string? filter)
    {
        if (content.IsNullOrEmpty())
        {
            return 0;
        }

        var filterText = filter.TrimEx();
        var count = 0;
        foreach (var line in content
            .Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Distinct())
        {
            var item = FmtHandler.ResolveConfig(line, out _);
            if (item == null)
            {
                continue;
            }

            if (filterText.IsNotEmpty()
                && !Regex.IsMatch(item.Remarks, filterText))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    private static int CountInnerLinks(string content)
    {
        if (content.IsNullOrEmpty())
        {
            return 0;
        }

        return content
            .Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Distinct()
            .Count(t => t.StartsWith(Global.InnerUriProtocol, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountCustomConfigs(string content)
    {
        if (content.IsNullOrEmpty() || HtmlPageFmt.IsHtmlPage(content))
        {
            return 0;
        }

        if (ShadowsocksFmt.ResolveSip008(content)?.Count > 0)
        {
            return 1;
        }

        if (content.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            && content.Contains("[Peer]", StringComparison.OrdinalIgnoreCase)
            && WireguardFmt.ResolveConfig(content)?.Count > 0)
        {
            return 1;
        }

        var json = JsonUtils.ParseJson(content);
        if (json is JsonArray jsonArray)
        {
            var count = 0;
            foreach (var node in jsonArray)
            {
                if (node == null)
                {
                    continue;
                }

                var item = node.ToJsonString();
                if (LooksLikeSingboxConfig(item)
                    || LooksLikeV2rayConfig(item))
                {
                    count++;
                }
            }
            return count;
        }

        if (LooksLikeSingboxConfig(content)
            || LooksLikeV2rayConfig(content)
            || LooksLikeClashConfig(content)
            || LooksLikeHysteria2Config(content))
        {
            return 1;
        }

        return 0;
    }

    private static bool LooksLikeSingboxConfig(string content)
    {
        var config = JsonUtils.ParseJson(content);
        return config?["inbounds"] != null
               && config["outbounds"] != null
               && config["route"] != null
               && config["dns"] != null;
    }

    private static bool LooksLikeV2rayConfig(string content)
    {
        var config = JsonUtils.ParseJson(content);
        return config?["inbounds"] != null
               && config["outbounds"] != null
               && config["routing"] != null;
    }

    private static bool LooksLikeClashConfig(string content)
    {
        return content.Contains("rules", StringComparison.OrdinalIgnoreCase)
               && content.Contains("-port", StringComparison.OrdinalIgnoreCase)
               && content.Contains("proxies", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeHysteria2Config(string content)
    {
        return content.Contains("server", StringComparison.OrdinalIgnoreCase)
               && content.Contains("auth", StringComparison.OrdinalIgnoreCase)
               && content.Contains("up", StringComparison.OrdinalIgnoreCase)
               && content.Contains("down", StringComparison.OrdinalIgnoreCase)
               && content.Contains("listen", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyErrorResponse(string content)
    {
        if (content.IsNullOrEmpty())
        {
            return true;
        }

        if (HtmlPageFmt.IsHtmlPage(content))
        {
            return true;
        }

        var trimmed = content.Trim();
        if (trimmed.Length <= 256)
        {
            var lower = trimmed.ToLowerInvariant();
            return lower.Contains("error")
                   || lower.Contains("rate limit")
                   || lower.Contains("too many requests")
                   || lower.Contains("forbidden")
                   || lower.Contains("unauthorized")
                   || lower.Contains("not found");
        }

        return false;
    }
}
