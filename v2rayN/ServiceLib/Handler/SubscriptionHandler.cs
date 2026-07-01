namespace ServiceLib.Handler;

public static class SubscriptionHandler
{
    public static async Task UpdateProcess(Config config, string subId, bool blProxy, Func<bool, string, Task> updateFunc)
    {
        await updateFunc?.Invoke(false, ResUI.MsgUpdateSubscriptionStart);
        var subItem = await AppManager.Instance.SubItems();

        if (subItem is not { Count: > 0 })
        {
            await updateFunc?.Invoke(false, ResUI.MsgNoValidSubscription);
            return;
        }

        var successCount = 0;
        foreach (var item in subItem)
        {
            try
            {
                if (!IsValidSubscription(item, subId))
                {
                    continue;
                }

                var hashCode = $"{item.Remarks}->";
                if (item.Enabled == false)
                {
                    await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgSkipSubscriptionUpdate}");
                    continue;
                }

                // Create download handler
                var downloadHandle = CreateDownloadHandler(hashCode, updateFunc);
                await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgStartGettingSubscriptions}");

                // Get all subscription content (main subscription + additional subscriptions)
                var result = await DownloadAllSubscriptions(config, item, blProxy, downloadHandle, hashCode, updateFunc);

                // Process download result
                if (await ProcessDownloadResult(config, item.Id, result, hashCode, updateFunc))
                {
                    successCount++;
                    item.UpdateTime = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds();
                    await ConfigHandler.AddSubItem(config, item);
                }

                await updateFunc?.Invoke(false, "-------------------------------------------------------");
            }
            catch (Exception ex)
            {
                var hashCode = $"{item.Remarks}->";
                Logging.SaveLog("UpdateSubscription", ex);
                await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgFailedImportSubscription}: {ex.Message}");
                await updateFunc?.Invoke(false, "-------------------------------------------------------");
            }
        }

        await updateFunc?.Invoke(successCount > 0, $"{ResUI.MsgUpdateSubscriptionEnd}");
    }

    private static bool IsValidSubscription(SubItem item, string subId)
    {
        var id = item.Id.TrimEx();
        var url = item.Url.TrimEx();

        if (id.IsNullOrEmpty() || url.IsNullOrEmpty())
        {
            return false;
        }

        if (subId.IsNotEmpty() && item.Id != subId)
        {
            return false;
        }

        if (!url.StartsWith(Global.HttpsProtocol) && !url.StartsWith(Global.HttpProtocol))
        {
            return false;
        }

        return true;
    }

    private static DownloadService CreateDownloadHandler(string hashCode, Func<bool, string, Task> updateFunc)
    {
        var downloadHandle = new DownloadService();
        downloadHandle.Error += (sender2, args) =>
        {
            updateFunc?.Invoke(false, $"{hashCode}{args.GetException().Message}");
        };
        return downloadHandle;
    }

    private static async Task<string> DownloadSubscriptionContent(DownloadService downloadHandle, string url, bool blProxy, string userAgent)
    {
        var result = await downloadHandle.TryDownloadString(url, blProxy, userAgent);

        // If download with proxy fails, try direct connection
        if (blProxy && result.IsNullOrEmpty())
        {
            result = await downloadHandle.TryDownloadString(url, false, userAgent);
        }

        return result ?? string.Empty;
    }

    private static async Task<string> DownloadAllSubscriptions(Config config, SubItem item, bool blProxy, DownloadService downloadHandle, string hashCode, Func<bool, string, Task> updateFunc)
    {
        // Download main subscription content
        var result = await DownloadMainSubscription(config, item, blProxy, downloadHandle, hashCode, updateFunc);

        // Process additional subscription links (if any)
        if (item.ConvertTarget.IsNullOrEmpty() && item.MoreUrl.TrimEx().IsNotEmpty())
        {
            result = await DownloadAdditionalSubscriptions(item, result, blProxy, downloadHandle, hashCode, updateFunc);
        }

        return result;
    }

    private static async Task<string> DownloadMainSubscription(Config config, SubItem item, bool blProxy, DownloadService downloadHandle, string hashCode, Func<bool, string, Task> updateFunc)
    {
        // Prepare subscription URL and download directly
        var url = Utils.GetPunycode(item.Url.TrimEx());

        // If conversion is needed
        if (item.ConvertTarget.IsNotEmpty())
        {
            var subConvertUrl = config.ConstItem.SubConvertUrl.IsNullOrEmpty()
                ? Global.SubConvertUrls.FirstOrDefault()
                : config.ConstItem.SubConvertUrl;

            url = string.Format(subConvertUrl!, Utils.UrlEncode(url));

            if (!url.Contains("target="))
            {
                url += $"&target={item.ConvertTarget}";
            }

            if (!url.Contains("config="))
            {
                url += $"&config={Global.SubConvertConfig.FirstOrDefault()}";
            }
        }

        // Download and return result directly
        if (!SubscriptionAutoDetector.IsAutoUserAgent(item.UserAgent))
        {
            return await DownloadSubscriptionContent(downloadHandle, url, blProxy, item.UserAgent);
        }

        return await DownloadSubscriptionContentAuto(downloadHandle, url, blProxy, item, true, hashCode, updateFunc);
    }

    private static async Task<string> DownloadSubscriptionContentAuto(DownloadService downloadHandle, string url, bool blProxy, SubItem item, bool persistDetectedUserAgent, string hashCode, Func<bool, string, Task> updateFunc)
    {
        // Restore in-memory cache from persisted DetectedUserAgent
        if (persistDetectedUserAgent && item.DetectedUserAgent.IsNotEmpty())
        {
            SubscriptionAutoDetector.SavePreferredUserAgent(url, SubscriptionAutoDetector.FromPersistedUserAgent(item.DetectedUserAgent), item.Filter);
        }

        // ── Cached UA path with cross-family probe ────────────────────
        if (SubscriptionAutoDetector.TryGetPreferredUserAgent(url, item.Filter, out var cachedUserAgent))
        {
            return await DownloadWithCrossProbe(downloadHandle, url, blProxy, item, persistDetectedUserAgent, hashCode, updateFunc, cachedUserAgent);
        }

        // ── No cache: full candidate traversal ───────────────────────
        return await DownloadWithFullTraversal(downloadHandle, url, blProxy, item, persistDetectedUserAgent, hashCode, updateFunc);
    }

    /// <summary>
    /// Cached UA path: download with cached UA and cross-probe one representative
    /// from a different protocol family in parallel, then pick the best.
    /// </summary>
    private static async Task<string> DownloadWithCrossProbe(
        DownloadService downloadHandle, string url, bool blProxy, SubItem item,
        bool persistDetectedUserAgent, string hashCode, Func<bool, string, Task> updateFunc,
        string cachedUserAgent)
    {
        var crossProbeUA = SubscriptionAutoDetector.GetCrossProbeUserAgent(cachedUserAgent);

        // Launch both downloads in parallel
        var cachedTask = DownloadSubscriptionContent(downloadHandle, url, blProxy, cachedUserAgent);
        var crossProbeTask = crossProbeUA.IsNotEmpty()
            ? DownloadSubscriptionContent(downloadHandle, url, blProxy, crossProbeUA)
            : Task.FromResult(string.Empty);

        await Task.WhenAll(cachedTask, crossProbeTask);

        var cachedResult = await cachedTask;
        var crossProbeResult = await crossProbeTask;

        // Score the cached result
        var cachedDetect = cachedResult.IsNotEmpty()
            ? SubscriptionAutoDetector.Detect(cachedUserAgent, cachedResult, item.Filter)
            : null;

        // Score the cross-probe result
        var crossProbeDetect = crossProbeResult.IsNotEmpty()
            ? SubscriptionAutoDetector.Detect(crossProbeUA, crossProbeResult, item.Filter)
            : null;

        // Pick the best result
        SubscriptionDetectResult? bestResult = null;
        string bestContent = string.Empty;

        if (cachedDetect != null && cachedDetect.Score > 0)
        {
            bestResult = cachedDetect;
            bestContent = cachedResult;
        }

        if (crossProbeDetect != null && crossProbeDetect.Score > 0
            && (bestResult == null || crossProbeDetect.Score > bestResult.Score))
        {
            bestResult = crossProbeDetect;
            bestContent = crossProbeResult;
        }

        if (bestResult == null)
        {
            // Both failed; fall through to full traversal
            return await DownloadWithFullTraversal(downloadHandle, url, blProxy, item, persistDetectedUserAgent, hashCode, updateFunc);
        }

        // Log results
        var cachedNodeCount = cachedDetect?.TotalNodeCount ?? 0;
        var crossProbeNodeCount = crossProbeDetect?.TotalNodeCount ?? 0;
        await updateFunc?.Invoke(false, $"{hashCode}Auto User-Agent cached {DisplayAutoUserAgent(cachedUserAgent)} ({cachedNodeCount} nodes)");
        if (crossProbeUA.IsNotEmpty() && crossProbeDetect != null)
        {
            await updateFunc?.Invoke(false, $"{hashCode}Auto User-Agent cross-probe {DisplayAutoUserAgent(crossProbeUA)} ({crossProbeNodeCount} nodes)");
        }

        // Update cache if cross-probe won (or first time persisting)
        if (bestResult.UserAgent != cachedUserAgent || item.DetectedNodeCount != bestResult.TotalNodeCount)
        {
            SubscriptionAutoDetector.SavePreferredUserAgent(url, bestResult.UserAgent, item.Filter);
            if (persistDetectedUserAgent)
            {
                item.DetectedUserAgent = SubscriptionAutoDetector.ToPersistedUserAgent(bestResult.UserAgent);
                item.DetectedNodeCount = bestResult.TotalNodeCount;
                await SQLiteHelper.Instance.UpdateAsync(item);
            }
        }

        await updateFunc?.Invoke(false, $"{hashCode}Auto User-Agent selected {DisplayAutoUserAgent(bestResult.UserAgent)} ({bestResult.TotalNodeCount} nodes)");
        return bestContent;
    }

    /// <summary>
    /// No-cache path: try all candidate UAs sequentially and pick the best.
    /// </summary>
    private static async Task<string> DownloadWithFullTraversal(
        DownloadService downloadHandle, string url, bool blProxy, SubItem item,
        bool persistDetectedUserAgent, string hashCode, Func<bool, string, Task> updateFunc)
    {
        SubscriptionDetectResult? bestResult = null;
        var triedUserAgents = new HashSet<string>();

        foreach (var userAgent in SubscriptionAutoDetector.GetCandidateUserAgents(url, item.Filter))
        {
            if (!triedUserAgents.Add(userAgent))
            {
                continue;
            }

            var result = await DownloadSubscriptionContent(downloadHandle, url, blProxy, userAgent);
            if (result.IsNullOrEmpty())
            {
                continue;
            }

            var detectResult = SubscriptionAutoDetector.Detect(userAgent, result, item.Filter);
            bestResult ??= detectResult;
            if (detectResult.Score > bestResult.Score)
            {
                bestResult = detectResult;
            }

            await updateFunc?.Invoke(false, $"{hashCode}Auto User-Agent {DisplayAutoUserAgent(userAgent)}: {detectResult.TotalNodeCount} nodes");
        }

        if (bestResult == null)
        {
            return string.Empty;
        }

        if (bestResult.Score > 0)
        {
            SubscriptionAutoDetector.SavePreferredUserAgent(url, bestResult.UserAgent, item.Filter);
            if (persistDetectedUserAgent)
            {
                item.DetectedUserAgent = SubscriptionAutoDetector.ToPersistedUserAgent(bestResult.UserAgent);
                item.DetectedNodeCount = bestResult.TotalNodeCount;
                await SQLiteHelper.Instance.UpdateAsync(item);
            }
        }

        await updateFunc?.Invoke(false, $"{hashCode}Auto User-Agent selected {DisplayAutoUserAgent(bestResult.UserAgent)} ({bestResult.TotalNodeCount} nodes)");
        return bestResult.Content;
    }

    private static string DisplayAutoUserAgent(string userAgent)
    {
        return userAgent.IsNullOrEmpty() ? "default" : userAgent;
    }

    private static async Task<string> DownloadAdditionalSubscriptions(SubItem item, string mainResult, bool blProxy, DownloadService downloadHandle, string hashCode, Func<bool, string, Task> updateFunc)
    {
        var result = mainResult;

        // If main subscription result is Base64 encoded, decode it first
        if (result.IsNotEmpty() && Utils.IsBase64String(result))
        {
            result = Utils.Base64Decode(result);
        }

        // Process additional URL list
        var lstUrl = item.MoreUrl.TrimEx().Split(",") ?? [];
        foreach (var it in lstUrl)
        {
            var url2 = Utils.GetPunycode(it);
            if (url2.IsNullOrEmpty())
            {
                continue;
            }

            var additionalResult = SubscriptionAutoDetector.IsAutoUserAgent(item.UserAgent)
                ? await DownloadSubscriptionContentAuto(downloadHandle, url2, blProxy, item, false, hashCode, updateFunc)
                : await DownloadSubscriptionContent(downloadHandle, url2, blProxy, item.UserAgent);

            if (additionalResult.IsNotEmpty())
            {
                // Process additional subscription results, add to main result
                if (Utils.IsBase64String(additionalResult))
                {
                    result += Environment.NewLine + Utils.Base64Decode(additionalResult);
                }
                else
                {
                    result += Environment.NewLine + additionalResult;
                }
            }
        }

        return result;
    }

    private static async Task<bool> ProcessDownloadResult(Config config, string id, string result, string hashCode, Func<bool, string, Task> updateFunc)
    {
        if (result.IsNullOrEmpty())
        {
            await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgSubscriptionDecodingFailed}");
            return false;
        }

        await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgGetSubscriptionSuccessfully}");

        // If result is too short, display content directly
        if (result.Length < 99)
        {
            await updateFunc?.Invoke(false, $"{hashCode}{result}");
        }

        await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgStartParsingSubscription}");

        // Add servers to configuration
        var ret = await ConfigHandler.AddBatchServers(config, result, id, true);
        if (ret <= 0)
        {
            Logging.SaveLog("FailedImportSubscription");
            Logging.SaveLog(result);
        }

        // Update completion message
        await updateFunc?.Invoke(false, ret > 0
                ? $"{hashCode}{ResUI.MsgUpdateSubscriptionEnd}"
                : $"{hashCode}{ResUI.MsgFailedImportSubscription}");

        return ret > 0;
    }
}
