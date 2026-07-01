using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceLib.Common;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;

namespace ServiceLib.Handler.Fmt;

public class ClashFmt : BaseFmt
{
    private static readonly string _tag = "ClashFmt";

    /// <summary>
    /// Parse a full Clash YAML config into a single Custom profile (mihomo core).
    /// This is the fallback when individual proxy extraction is not possible or not desired.
    /// </summary>
    public static ProfileItem? ResolveFull(string strData, string? subRemarks)
    {
        if (Contains(strData, "rules", "-port", "proxies"))
        {
            var fileName = WriteAllText(strData, "yaml");

            var profileItem = new ProfileItem
            {
                CoreType = ECoreType.mihomo,
                Address = fileName,
                Remarks = subRemarks ?? "clash_custom"
            };
            return profileItem;
        }

        return null;
    }

    /// <summary>
    /// Parse Clash YAML proxies list into individual ProfileItem entries.
    /// Each proxy is converted to a standard share link URI, then parsed
    /// by FmtHandler.ResolveConfig — this reuses all existing field mapping
    /// logic and ensures full compatibility with sing-box / mihomo / Xray cores.
    /// <para>
    /// Supported Clash proxy types: vmess, vless, trojan, ss/shadowsocks,
    /// hysteria2/hy2, tuic, wireguard, socks5/socks, anytls, mieru.
    /// Unsupported types (snell, http, relay, etc.) are silently skipped.
    /// </para>
    /// </summary>
    public static List<ProfileItem>? ResolveFullArray(string strData, string? subRemarks)
    {
        if (!Contains(strData, "proxies"))
        {
            return null;
        }

        try
        {
            var clashConfig = YamlUtils.FromYaml<Dictionary<string, object>>(strData);
            if (clashConfig == null || !clashConfig.TryGetValue("proxies", out var proxiesObj))
            {
                return null;
            }

            if (proxiesObj is not List<object> proxiesList || proxiesList.Count == 0)
            {
                return null;
            }

            List<ProfileItem> lstResult = [];
            var skippedTypes = new HashSet<string>();

            foreach (var proxy in proxiesList)
            {
                if (proxy is not Dictionary<object, object> proxyDict)
                {
                    continue;
                }

                var shareLink = ClashProxyToShareLink(proxyDict);
                if (shareLink.IsNullOrEmpty())
                {
                    var skippedType = GetStr(proxyDict, "type");
                    if (skippedType.IsNotEmpty())
                    {
                        skippedTypes.Add(skippedType.ToLowerInvariant());
                    }
                    continue;
                }

                var profileItem = FmtHandler.ResolveConfig(shareLink, out var msg);
                if (profileItem != null)
                {
                    // Prefix remarks with subscription name if available
                    if (subRemarks.IsNotEmpty() && !profileItem.Remarks.StartsWith(subRemarks))
                    {
                        profileItem.Remarks = $"{subRemarks}-{profileItem.Remarks}";
                    }
                    lstResult.Add(profileItem);
                }
            }

            if (skippedTypes.Count > 0)
            {
                Logging.SaveLog($"ClashFmt: Skipped unsupported proxy types: {string.Join(", ", skippedTypes)}");
            }

            return lstResult.Count > 0 ? lstResult : null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    // ── Clash proxy → share link conversion ──────────────────────────

    private static string? ClashProxyToShareLink(Dictionary<object, object> proxy)
    {
        var type = GetStr(proxy, "type")?.ToLowerInvariant();
        if (type.IsNullOrEmpty()) return null;

        var server = GetStr(proxy, "server");
        var portStr = GetStr(proxy, "port");
        if (server.IsNullOrEmpty() || portStr.IsNullOrEmpty()) return null;
        if (!int.TryParse(portStr, out var port) || port <= 0) return null;

        var name = GetStr(proxy, "name") ?? "unnamed";

        return type switch
        {
            "vmess" => ToVmessShareLink(proxy, server, port, name),
            "vless" => ToVlessShareLink(proxy, server, port, name),
            "trojan" => ToTrojanShareLink(proxy, server, port, name),
            "ss" or "shadowsocks" => ToSsShareLink(proxy, server, port, name),
            "hysteria2" or "hy2" => ToHysteria2ShareLink(proxy, server, port, name),
            "tuic" => ToTuicShareLink(proxy, server, port, name),
            "wireguard" => ToWireguardShareLink(proxy, server, port, name),
            "socks5" or "socks" => ToSocksShareLink(proxy, server, port, name),
            "anytls" => ToAnytlsShareLink(proxy, server, port, name),
            "mieru" => ToMieruShareLink(proxy, server, port, name),
            _ => null, // unsupported: snell, http, relay, etc.
        };
    }

    // ── VMess ────────────────────────────────────────────────────────

    private static string? ToVmessShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var uuid = GetStr(proxy, "uuid");
        if (uuid.IsNullOrEmpty()) return null;

        var alterId = GetInt(proxy, "alterId");
        var cipher = GetStr(proxy, "cipher") ?? "auto";
        var network = GetStr(proxy, "network") ?? "tcp";
        var tls = GetBool(proxy, "tls");
        var sni = GetStr(proxy, "servername") ?? GetStr(proxy, "sni") ?? "";
        var alpnStr = GetAlpn(proxy);
        var fp = GetStr(proxy, "client-fingerprint") ?? GetStr(proxy, "fingerprint") ?? "";
        var skipCertVerify = GetBool(proxy, "skip-cert-verify");

        // Transport options
        string host = "";
        string path = "";

        switch (network)
        {
            case "ws":
                var wsOpts = GetDict(proxy, "ws-opts");
                if (wsOpts != null)
                {
                    path = GetStr(wsOpts, "path") ?? "/";
                    var headers = GetDict(wsOpts, "headers");
                    host = headers != null ? GetStr(headers, "Host") ?? "" : "";
                }
                break;
            case "grpc":
                var grpcOpts = GetDict(proxy, "grpc-opts");
                if (grpcOpts != null)
                {
                    path = GetStr(grpcOpts, "grpc-service-name") ?? "";
                }
                break;
            case "h2" or "http":
                var h2Opts = GetDict(proxy, "h2-opts");
                if (h2Opts != null)
                {
                    path = GetStr(h2Opts, "path") ?? "/";
                    var hosts = GetList(h2Opts, "host");
                    host = hosts?.Count > 0 ? hosts[0]?.ToString() ?? "" : "";
                }
                // v2rayN share links don't support h2 as a network type; map to tcp (raw)
                network = Global.RawNetworkAlias;
                break;
            default:
                // tcp or empty → raw
                network = Global.RawNetworkAlias;
                break;
        }

        // Reality options (rare for VMess but possible)
        var realityOpts = GetDict(proxy, "reality-opts");
        var pbk = realityOpts != null ? GetStr(realityOpts, "public-key") ?? "" : "";
        var sid = realityOpts != null ? GetStr(realityOpts, "short-id") ?? "" : "";

        var vmessObj = new VmessQRCode
        {
            v = 2,
            ps = name,
            add = server,
            port = port,
            id = uuid,
            aid = alterId,
            scy = cipher,
            net = network,
            type = "none",
            host = host,
            path = path,
            tls = tls ? Global.StreamSecurity : "",
            sni = sni,
            alpn = alpnStr,
            fp = fp,
            insecure = skipCertVerify ? "1" : "0",
        };

        var json = JsonSerializer.Serialize(vmessObj, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        });
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return $"{Global.ProtocolShares[EConfigType.VMess]}{base64}";
    }

    // ── VLESS ────────────────────────────────────────────────────────

    private static string? ToVlessShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var uuid = GetStr(proxy, "uuid");
        if (uuid.IsNullOrEmpty()) return null;

        var remark = Utils.UrlEncode(name);
        var dicQuery = new Dictionary<string, string>
        {
            ["encryption"] = "none"
        };

        // Flow
        var flow = GetStr(proxy, "flow") ?? "";
        if (flow.IsNotEmpty())
        {
            dicQuery["flow"] = flow;
        }

        // Security / TLS
        var realityOpts = GetDict(proxy, "reality-opts");
        var tls = GetBool(proxy, "tls");
        var pbk = realityOpts != null ? GetStr(realityOpts, "public-key") ?? "" : "";
        var sid = realityOpts != null ? GetStr(realityOpts, "short-id") ?? "" : "";
        var spx = GetStr(proxy, "spider-x") ?? GetStr(proxy, "spiderx") ?? "";

        if (realityOpts != null && pbk.IsNotEmpty())
        {
            dicQuery["security"] = Global.StreamSecurityReality;
            dicQuery["pbk"] = Utils.UrlEncode(pbk);
            if (sid.IsNotEmpty()) dicQuery["sid"] = Utils.UrlEncode(sid);
            if (spx.IsNotEmpty()) dicQuery["spx"] = Utils.UrlEncode(spx);
        }
        else if (tls)
        {
            dicQuery["security"] = Global.StreamSecurity;
        }
        else
        {
            dicQuery["security"] = "none";
        }

        // SNI
        var sni = GetStr(proxy, "servername") ?? GetStr(proxy, "sni") ?? "";
        if (sni.IsNotEmpty()) dicQuery["sni"] = Utils.UrlEncode(sni);

        // ALPN
        var alpnStr = GetAlpn(proxy);
        if (alpnStr.IsNotEmpty()) dicQuery["alpn"] = Utils.UrlEncode(alpnStr);

        // Fingerprint
        var fp = GetStr(proxy, "client-fingerprint") ?? GetStr(proxy, "fingerprint") ?? "";
        if (fp.IsNotEmpty()) dicQuery["fp"] = Utils.UrlEncode(fp);

        // Allow insecure
        if (GetBool(proxy, "skip-cert-verify"))
        {
            dicQuery["insecure"] = "1";
        }

        // Network / Transport
        var network = GetStr(proxy, "network") ?? "tcp";
        AddTransportQuery(proxy, network, dicQuery);

        var query = "?" + string.Join("&", dicQuery.Select(x => $"{x.Key}={x.Value}"));
        return $"{Global.ProtocolShares[EConfigType.VLESS]}{uuid}@{GetIpv6(server)}:{port}{query}#{remark}";
    }

    // ── Trojan ───────────────────────────────────────────────────────

    private static string? ToTrojanShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var password = GetStr(proxy, "password");
        if (password.IsNullOrEmpty()) return null;

        var remark = Utils.UrlEncode(name);
        var dicQuery = new Dictionary<string, string>();

        // Security
        var realityOpts = GetDict(proxy, "reality-opts");
        var tls = GetBool(proxy, "tls");
        var pbk = realityOpts != null ? GetStr(realityOpts, "public-key") ?? "" : "";
        var sid = realityOpts != null ? GetStr(realityOpts, "short-id") ?? "" : "";

        if (realityOpts != null && pbk.IsNotEmpty())
        {
            dicQuery["security"] = Global.StreamSecurityReality;
            dicQuery["pbk"] = Utils.UrlEncode(pbk);
            if (sid.IsNotEmpty()) dicQuery["sid"] = Utils.UrlEncode(sid);
        }
        else
        {
            dicQuery["security"] = Global.StreamSecurity; // trojan always uses tls
        }

        // SNI
        var sni = GetStr(proxy, "sni") ?? GetStr(proxy, "servername") ?? "";
        if (sni.IsNotEmpty()) dicQuery["sni"] = Utils.UrlEncode(sni);

        // ALPN
        var alpnStr = GetAlpn(proxy);
        if (alpnStr.IsNotEmpty()) dicQuery["alpn"] = Utils.UrlEncode(alpnStr);

        // Fingerprint
        var fp = GetStr(proxy, "client-fingerprint") ?? GetStr(proxy, "fingerprint") ?? "";
        if (fp.IsNotEmpty()) dicQuery["fp"] = Utils.UrlEncode(fp);

        // Allow insecure
        if (GetBool(proxy, "skip-cert-verify"))
        {
            dicQuery["insecure"] = "1";
        }

        // Network / Transport
        var network = GetStr(proxy, "network") ?? "tcp";
        AddTransportQuery(proxy, network, dicQuery);

        var query = "?" + string.Join("&", dicQuery.Select(x => $"{x.Key}={x.Value}"));
        return $"{Global.ProtocolShares[EConfigType.Trojan]}{Utils.UrlEncode(password)}@{GetIpv6(server)}:{port}{query}#{remark}";
    }

    // ── Shadowsocks ──────────────────────────────────────────────────

    private static string? ToSsShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var cipher = GetStr(proxy, "cipher");
        var password = GetStr(proxy, "password");
        if (cipher.IsNullOrEmpty() || password.IsNullOrEmpty()) return null;

        var remark = Utils.UrlEncode(name);
        var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cipher}:{password}")).TrimEnd('=');
        return $"{Global.ProtocolShares[EConfigType.Shadowsocks]}{userInfo}@{GetIpv6(server)}:{port}#{remark}";
    }

    // ── Hysteria2 ────────────────────────────────────────────────────

    private static string? ToHysteria2ShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var password = GetStr(proxy, "password");
        if (password.IsNullOrEmpty()) return null;

        var remark = Utils.UrlEncode(name);
        var dicQuery = new Dictionary<string, string>();

        // SNI
        var sni = GetStr(proxy, "sni") ?? GetStr(proxy, "servername") ?? "";
        if (sni.IsNotEmpty()) dicQuery["sni"] = Utils.UrlEncode(sni);

        // Allow insecure
        if (GetBool(proxy, "skip-cert-verify"))
        {
            dicQuery["insecure"] = "1";
        }

        // Obfs
        var obfs = GetStr(proxy, "obfs") ?? "";
        var obfsPassword = GetStr(proxy, "obfs-password") ?? "";
        if (obfs.IsNotEmpty())
        {
            dicQuery["obfs"] = Utils.UrlEncode(obfs);
            if (obfsPassword.IsNotEmpty())
            {
                dicQuery["obfs-password"] = Utils.UrlEncode(obfsPassword);
            }
        }

        // Pin SHA256
        var pinSHA256 = GetStr(proxy, "pinSHA256") ?? "";
        if (pinSHA256.IsNotEmpty())
        {
            dicQuery["pinSHA256"] = Utils.UrlEncode(pinSHA256);
        }

        var query = dicQuery.Count > 0 ? "?" + string.Join("&", dicQuery.Select(x => $"{x.Key}={x.Value}")) : "";
        return $"{Global.ProtocolShares[EConfigType.Hysteria2]}{Utils.UrlEncode(password)}@{GetIpv6(server)}:{port}{query}#{remark}";
    }

    // ── TUIC ─────────────────────────────────────────────────────────

    private static string? ToTuicShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var uuid = GetStr(proxy, "uuid");
        var password = GetStr(proxy, "password");
        if (uuid.IsNullOrEmpty() || password.IsNullOrEmpty()) return null;

        var remark = Utils.UrlEncode(name);
        var dicQuery = new Dictionary<string, string>();

        // SNI
        var sni = GetStr(proxy, "sni") ?? GetStr(proxy, "servername") ?? "";
        if (sni.IsNotEmpty()) dicQuery["sni"] = Utils.UrlEncode(sni);

        // Allow insecure
        if (GetBool(proxy, "skip-cert-verify"))
        {
            dicQuery["insecure"] = "1";
        }

        // Congestion control
        var congestionControl = GetStr(proxy, "congestion-control") ?? GetStr(proxy, "congestion_control") ?? "";
        if (congestionControl.IsNotEmpty())
        {
            dicQuery["congestion_control"] = congestionControl;
        }

        // ALPN
        var alpnStr = GetAlpn(proxy);
        if (alpnStr.IsNotEmpty()) dicQuery["alpn"] = Utils.UrlEncode(alpnStr);

        var query = dicQuery.Count > 0 ? "?" + string.Join("&", dicQuery.Select(x => $"{x.Key}={x.Value}")) : "";
        var userInfo = $"{Utils.UrlEncode(uuid)}:{Utils.UrlEncode(password)}";
        return $"{Global.ProtocolShares[EConfigType.TUIC]}{userInfo}@{GetIpv6(server)}:{port}{query}#{remark}";
    }

    // ── WireGuard ────────────────────────────────────────────────────

    private static string? ToWireguardShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var privateKey = GetStr(proxy, "private-key");
        if (privateKey.IsNullOrEmpty()) return null;

        var remark = Utils.UrlEncode(name);
        var dicQuery = new Dictionary<string, string>();

        // Public key (from top-level or from peers)
        var publicKey = GetStr(proxy, "public-key") ?? "";
        if (publicKey.IsNullOrEmpty())
        {
            var peers = GetList(proxy, "peers");
            if (peers?.Count > 0 && peers[0] is Dictionary<object, object> firstPeer)
            {
                publicKey = GetStr(firstPeer, "public-key") ?? "";
            }
        }
        if (publicKey.IsNotEmpty()) dicQuery["publickey"] = Utils.UrlEncode(publicKey);

        // Pre-shared key
        var presharedKey = GetStr(proxy, "preshared-key") ?? "";
        if (presharedKey.IsNullOrEmpty())
        {
            var peers2 = GetList(proxy, "peers");
            if (peers2?.Count > 0 && peers2[0] is Dictionary<object, object> firstPeer2)
            {
                presharedKey = GetStr(firstPeer2, "preshared-key") ?? "";
            }
        }
        if (presharedKey.IsNotEmpty()) dicQuery["presharedkey"] = Utils.UrlEncode(presharedKey);

        // Reserved
        var reserved = GetStr(proxy, "reserved") ?? "";
        if (reserved.IsNullOrEmpty())
        {
            var reservedList = GetList(proxy, "reserved");
            if (reservedList?.Count > 0)
            {
                reserved = string.Join(",", reservedList.Select(x => x?.ToString() ?? "0"));
            }
        }
        if (reserved.IsNotEmpty()) dicQuery["reserved"] = Utils.UrlEncode(reserved);

        // IP / Interface address
        var ip = GetStr(proxy, "ip") ?? "";
        if (ip.IsNotEmpty()) dicQuery["address"] = Utils.UrlEncode(ip);

        // MTU
        var mtu = GetInt(proxy, "mtu");
        if (mtu > 0) dicQuery["mtu"] = mtu.ToString();

        var query = dicQuery.Count > 0 ? "?" + string.Join("&", dicQuery.Select(x => $"{x.Key}={x.Value}")) : "";
        return $"{Global.ProtocolShares[EConfigType.WireGuard]}{Utils.UrlEncode(privateKey)}@{GetIpv6(server)}:{port}{query}#{remark}";
    }

    // ── Mieru ────────────────────────────────────────────────────────

    private static string? ToMieruShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var password = GetStr(proxy, "password");
        if (password.IsNullOrEmpty()) return null;

        var remark = Utils.UrlEncode(name);
        var username = GetStr(proxy, "username") ?? "";
        var dicQuery = new Dictionary<string, string>();

        // Transport: TCP or UDP
        var transport = GetStr(proxy, "transport") ?? "TCP";
        dicQuery["transport"] = transport.ToUpperInvariant();

        // Multiplexing
        var multiplexing = GetStr(proxy, "multiplexing") ?? "";
        if (multiplexing.IsNotEmpty())
        {
            dicQuery["multiplexing"] = multiplexing;
        }

        // Port range (cannot coexist with port)
        var portRange = GetStr(proxy, "port-range") ?? "";
        if (portRange.IsNotEmpty())
        {
            dicQuery["port-range"] = Utils.UrlEncode(portRange);
        }

        // Traffic pattern
        var trafficPattern = GetStr(proxy, "traffic-pattern") ?? "";
        if (trafficPattern.IsNotEmpty())
        {
            dicQuery["traffic-pattern"] = Utils.UrlEncode(trafficPattern);
        }

        // Security: mieru always uses TLS
        dicQuery["security"] = Global.StreamSecurity;

        // SNI
        var sni = GetStr(proxy, "sni") ?? GetStr(proxy, "servername") ?? "";
        if (sni.IsNotEmpty()) dicQuery["sni"] = Utils.UrlEncode(sni);

        // Allow insecure
        if (GetBool(proxy, "skip-cert-verify"))
        {
            dicQuery["allowInsecure"] = "1";
        }

        var query = "?" + string.Join("&", dicQuery.Select(x => $"{x.Key}={x.Value}"));
        var userInfo = username.IsNotEmpty()
            ? $"{Utils.UrlEncode(username)}:{Utils.UrlEncode(password)}"
            : Utils.UrlEncode(password);

        return $"{Global.ProtocolShares[EConfigType.Mieru]}{userInfo}@{GetIpv6(server)}:{port}{query}#{remark}";
    }

    // ── SOCKS5 ───────────────────────────────────────────────────────

    private static string? ToSocksShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var remark = Utils.UrlEncode(name);
        var username = GetStr(proxy, "username") ?? "";
        var password = GetStr(proxy, "password") ?? "";

        string userInfo;
        if (username.IsNotEmpty() && password.IsNotEmpty())
        {
            userInfo = Utils.Base64Encode($"{username}:{password}", true);
        }
        else
        {
            userInfo = Utils.Base64Encode(":");
        }

        return $"{Global.ProtocolShares[EConfigType.SOCKS]}{userInfo}@{GetIpv6(server)}:{port}#{remark}";
    }

    // ── AnyTLS ───────────────────────────────────────────────────────

    private static string? ToAnytlsShareLink(Dictionary<object, object> proxy, string server, int port, string name)
    {
        var password = GetStr(proxy, "password");
        if (password.IsNullOrEmpty()) return null;

        var remark = Utils.UrlEncode(name);
        var dicQuery = new Dictionary<string, string>();

        // Security: anytls always uses TLS
        dicQuery["security"] = Global.StreamSecurity;

        // SNI
        var sni = GetStr(proxy, "sni") ?? GetStr(proxy, "servername") ?? "";
        if (sni.IsNotEmpty()) dicQuery["sni"] = Utils.UrlEncode(sni);

        // ALPN
        var alpnStr = GetAlpn(proxy);
        if (alpnStr.IsNotEmpty()) dicQuery["alpn"] = Utils.UrlEncode(alpnStr);

        // Fingerprint
        var fp = GetStr(proxy, "client-fingerprint") ?? GetStr(proxy, "fingerprint") ?? "";
        if (fp.IsNotEmpty()) dicQuery["fp"] = Utils.UrlEncode(fp);

        // Allow insecure
        if (GetBool(proxy, "insecure") || GetBool(proxy, "skip-cert-verify"))
        {
            dicQuery["allowInsecure"] = "1";
        }

        var query = "?" + string.Join("&", dicQuery.Select(x => $"{x.Key}={x.Value}"));
        return $"{Global.ProtocolShares[EConfigType.Anytls]}{Utils.UrlEncode(password)}@{GetIpv6(server)}:{port}{query}#{remark}";
    }

    // ── Transport query builder (shared by VLESS and Trojan) ─────────

    private static void AddTransportQuery(Dictionary<object, object> proxy, string network, Dictionary<string, string> dicQuery)
    {
        // Map network type for share link
        var netAlias = network switch
        {
            "tcp" or "" => Global.RawNetworkAlias,
            "h2" or "http" => Global.RawNetworkAlias, // h2 not supported as share link network type; fall back
            _ => network, // ws, grpc, kcp, etc.
        };
        dicQuery["type"] = netAlias;

        switch (network)
        {
            case "ws":
                var wsOpts = GetDict(proxy, "ws-opts");
                if (wsOpts != null)
                {
                    var wsPath = GetStr(wsOpts, "path") ?? "/";
                    var headers = GetDict(wsOpts, "headers");
                    var wsHost = headers != null ? GetStr(headers, "Host") ?? "" : "";
                    if (wsHost.IsNotEmpty()) dicQuery["host"] = Utils.UrlEncode(wsHost);
                    if (wsPath.IsNotEmpty()) dicQuery["path"] = Utils.UrlEncode(wsPath);
                }
                break;
            case "grpc":
                var grpcOpts = GetDict(proxy, "grpc-opts");
                if (grpcOpts != null)
                {
                    var serviceName = GetStr(grpcOpts, "grpc-service-name") ?? "";
                    if (serviceName.IsNotEmpty()) dicQuery["serviceName"] = Utils.UrlEncode(serviceName);
                }
                break;
            case "h2" or "http":
                var h2Opts = GetDict(proxy, "h2-opts");
                if (h2Opts != null)
                {
                    var h2Path = GetStr(h2Opts, "path") ?? "/";
                    var hosts = GetList(h2Opts, "host");
                    var h2Host = hosts?.Count > 0 ? hosts[0]?.ToString() ?? "" : "";
                    if (h2Host.IsNotEmpty()) dicQuery["host"] = Utils.UrlEncode(h2Host);
                    if (h2Path.IsNotEmpty()) dicQuery["path"] = Utils.UrlEncode(h2Path);
                }
                break;
        }
    }

    // ── YAML dictionary helper methods ───────────────────────────────

    private static string? GetStr(Dictionary<object, object> dict, string key)
    {
        if (dict == null) return null;
        // Try exact key first, then case-insensitive
        foreach (var k in dict.Keys)
        {
            var keyStr = k?.ToString();
            if (keyStr == key) return dict[k]?.ToString();
        }
        foreach (var k in dict.Keys)
        {
            var keyStr = k?.ToString();
            if (string.Equals(keyStr, key, StringComparison.OrdinalIgnoreCase)) return dict[k]?.ToString();
        }
        return null;
    }

    private static int GetInt(Dictionary<object, object> dict, string key)
    {
        var str = GetStr(dict, key);
        return int.TryParse(str, out var val) ? val : 0;
    }

    private static bool GetBool(Dictionary<object, object> dict, string key)
    {
        var str = GetStr(dict, key);
        if (str.IsNullOrEmpty()) return false;
        return str.Equals("true", StringComparison.OrdinalIgnoreCase)
               || str == "1";
    }

    private static Dictionary<object, object>? GetDict(Dictionary<object, object> dict, string key)
    {
        // Try exact key, then case-insensitive, also try with hyphen/underscore variants
        var keys = new[] { key, key.Replace("-", "_"), key.Replace("_", "-") };
        foreach (var k in keys)
        {
            foreach (var dk in dict.Keys)
            {
                var keyStr = dk?.ToString();
                if (string.Equals(keyStr, k, StringComparison.OrdinalIgnoreCase))
                {
                    return dict[dk] as Dictionary<object, object>;
                }
            }
        }
        return null;
    }

    private static List<object>? GetList(Dictionary<object, object> dict, string key)
    {
        foreach (var k in dict.Keys)
        {
            var keyStr = k?.ToString();
            if (string.Equals(keyStr, key, StringComparison.OrdinalIgnoreCase))
            {
                return dict[k] as List<object>;
            }
        }
        return null;
    }

    /// <summary>
    /// Extract ALPN value from Clash proxy. Clash uses a list (e.g. ["h2", "http/1.1"])
    /// but v2rayN share links use comma-separated string.
    /// </summary>
    private static string GetAlpn(Dictionary<object, object> proxy)
    {
        // Look up the raw alpn value directly to handle both string and list types
        foreach (var k in proxy.Keys)
        {
            if (string.Equals(k?.ToString(), "alpn", StringComparison.OrdinalIgnoreCase))
            {
                var val = proxy[k];
                if (val is List<object> alpnList)
                {
                    return string.Join(",", alpnList.Select(x => x?.ToString() ?? "").Where(x => x.IsNotEmpty()));
                }
                var strVal = val?.ToString();
                if (strVal.IsNotEmpty()) return strVal;
            }
        }

        return "";
    }
}
