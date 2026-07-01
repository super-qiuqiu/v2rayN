namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Generates mihomo YAML configuration for normal ProfileItem nodes.
/// Custom mihomo YAML files remain handled by <see cref="CoreConfigClashService" />.
/// </summary>
public class CoreConfigMihomoService(CoreConfigContext context)
{
    private static readonly string _tag = "CoreConfigMihomoService";
    private readonly Config _config = context.AppConfig;
    private readonly ProfileItem _node = context.Node;

    public RetResult GenerateClientConfigContent()
    {
        var ret = new RetResult();
        try
        {
            if (_node == null || !_node.IsValid())
            {
                ret.Msg = ResUI.CheckServerSettings;
                return ret;
            }

            ret.Msg = ResUI.InitialConfiguration;
            var proxyName = SafeProxyName(_node, Global.ProxyTag);
            var fileContent = CreateBaseConfig(AppManager.Instance.GetLocalPort(EInboundProtocol.socks));
            fileContent["proxies"] = BuildAllProxies(_node, proxyName);
            fileContent["rules"] = new List<string> { $"MATCH,{proxyName}" };

            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            ret.Success = true;
            ret.Data = YamlUtils.ToYaml(fileContent);
            return ret;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    public RetResult GenerateClientSpeedtestConfig(List<ServerTestItem> selecteds)
    {
        var ret = new RetResult();
        try
        {
            ret.Msg = ResUI.InitialConfiguration;

            var fileContent = CreateBaseConfig(null);
            var apiPort = Utils.GetFreePort();
            fileContent["external-controller"] = $"{Global.Loopback}:{apiPort}";
            var proxies = new List<Dictionary<string, object?>>();
            var listeners = new List<Dictionary<string, object?>>();
            var rules = new List<string>();
            var usedPorts = new HashSet<int>();
            var initPort = AppManager.Instance.GetLocalPort(EInboundProtocol.speedtest);

            foreach (var it in selecteds)
            {
                if (!(Global.MihomoSupportConfigType.Contains(it.ConfigType) || it.ConfigType.IsGroupType()))
                {
                    continue;
                }
                if (!it.ConfigType.IsComplexType() && it.Port <= 0)
                {
                    continue;
                }

                var actIndexId = context.ServerTestItemMap.GetValueOrDefault(it.IndexId, it.IndexId);
                var item = context.AllProxiesMap.GetValueOrDefault(actIndexId);
                if (item is null || item.ConfigType is EConfigType.Custom || !item.IsValid())
                {
                    continue;
                }

                var port = NextFreePort(initPort, usedPorts);
                initPort = port + 1;
                usedPorts.Add(port);
                it.Port = port;
                it.AllowTest = true;

                var proxyName = $"{Global.ProxyTag}{port}";
                var listenerName = $"mixed{port}";
                it.TestProxyName = proxyName;
                it.TestApiPort = apiPort;
                proxies.AddRange(BuildAllProxies(item, proxyName));
                listeners.Add(FilterNull(new Dictionary<string, object?>
                {
                    ["name"] = listenerName,
                    ["type"] = nameof(EInboundProtocol.mixed),
                    ["listen"] = Global.Loopback,
                    ["port"] = port,
                    ["udp"] = true,
                }));
                rules.Add($"IN-NAME,{listenerName},{proxyName}");
            }

            rules.Add("MATCH,DIRECT");
            fileContent["proxies"] = proxies;
            fileContent["listeners"] = listeners;
            fileContent["rules"] = rules;

            ret.Success = true;
            ret.Data = YamlUtils.ToYaml(fileContent);
            return ret;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    public RetResult GenerateClientSpeedtestConfig(int port, ServerTestItem? testItem = null)
    {
        var ret = new RetResult();
        try
        {
            if (_node == null || !_node.IsValid())
            {
                ret.Msg = ResUI.CheckServerSettings;
                return ret;
            }

            var proxyName = SafeProxyName(_node, Global.ProxyTag);
            var fileContent = CreateBaseConfig(port);
            var apiPort = Utils.GetFreePort();
            fileContent["external-controller"] = $"{Global.Loopback}:{apiPort}";
            if (testItem != null)
            {
                testItem.TestProxyName = proxyName;
                testItem.TestApiPort = apiPort;
            }
            fileContent["proxies"] = BuildAllProxies(_node, proxyName);
            fileContent["rules"] = new List<string> { $"MATCH,{proxyName}" };

            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            ret.Success = true;
            ret.Data = YamlUtils.ToYaml(fileContent);
            return ret;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    private Dictionary<string, object?> CreateBaseConfig(int? mixedPort)
    {
        var fileContent = new Dictionary<string, object?>
        {
            ["allow-lan"] = _config.Inbound.FirstOrDefault()?.AllowLANConn == true,
            ["bind-address"] = _config.Inbound.FirstOrDefault()?.AllowLANConn == true ? "*" : null,
            ["mode"] = _config.ClashUIItem.RuleMode == ERuleMode.Unchanged ? nameof(ERuleMode.Rule).ToLower() : _config.ClashUIItem.RuleMode.ToString().ToLower(),
            ["log-level"] = GetLogLevel(_config.CoreBasicItem.Loglevel),
            ["ipv6"] = _config.ClashUIItem.EnableIPv6,
        };
        if (mixedPort is > 0)
        {
            fileContent["mixed-port"] = mixedPort.Value;
        }
        if (_config.TunModeItem.EnableTun)
        {
            var tun = EmbedUtils.GetEmbedText(Global.ClashTunYaml);
            if (tun.IsNotEmpty())
            {
                var tunContent = YamlUtils.FromYaml<Dictionary<string, object>>(tun);
                if (tunContent != null && tunContent.TryGetValue("tun", out var tunValue))
                {
                    fileContent["tun"] = tunValue;
                }
            }
        }
        return FilterNull(fileContent);
    }

    private List<Dictionary<string, object?>> BuildAllProxies(ProfileItem node, string proxyName)
    {
        if (!node.ConfigType.IsComplexType())
        {
            return [BuildProxy(node, proxyName)];
        }
        return BuildGroupProxies(node, proxyName);
    }

    private List<Dictionary<string, object?>> BuildGroupProxies(ProfileItem node, string proxyName)
    {
        var proxies = new List<Dictionary<string, object?>>();
        var childNames = new List<string>();
        var childIds = Utils.String2List(node.GetProtocolExtra().ChildItems) ?? [];
        var i = 0;
        foreach (var childId in childIds)
        {
            if (!context.AllProxiesMap.TryGetValue(childId, out var child) || child.ConfigType == EConfigType.Custom)
            {
                continue;
            }

            var childName = childIds.Count == 1 ? proxyName : $"{proxyName}-{++i}-{SafeProxyName(child, child.IndexId)}";
            proxies.AddRange(BuildAllProxies(child, childName));
            childNames.Add(childName);
        }

        if (childNames.Count > 1)
        {
            var multipleLoad = node.GetProtocolExtra().MultipleLoad ?? EMultipleLoad.LeastPing;
            var groupType = multipleLoad == EMultipleLoad.Fallback ? "fallback" : "url-test";
            var group = FilterNull(new Dictionary<string, object?>
            {
                ["name"] = proxyName,
                ["type"] = groupType,
                ["proxies"] = childNames,
                ["url"] = _config.SpeedTestItem.SpeedPingTestUrl.NullIfEmpty() ?? Global.SpeedPingTestUrls.First(),
                ["interval"] = 300,
            });
            proxies.Insert(0, group);
        }
        return proxies;
    }

    private Dictionary<string, object?> BuildProxy(ProfileItem node, string name)
    {
        if (!Global.MihomoSupportConfigType.Contains(node.ConfigType))
        {
            throw new NotSupportedException($"mihomo does not support protocol {node.ConfigType}");
        }

        var proxy = node.ConfigType switch
        {
            EConfigType.VMess => BuildVmessProxy(node),
            EConfigType.VLESS => BuildVlessProxy(node),
            EConfigType.Shadowsocks => BuildShadowsocksProxy(node),
            EConfigType.Trojan => BuildTrojanProxy(node),
            EConfigType.Hysteria2 => BuildHysteria2Proxy(node),
            EConfigType.WireGuard => BuildWireGuardProxy(node),
            EConfigType.SOCKS => BuildSocksProxy(node),
            EConfigType.HTTP => BuildHttpProxy(node),
            EConfigType.Mieru => BuildMieruProxy(node),
            _ => throw new NotSupportedException($"mihomo does not support protocol {node.ConfigType}"),
        };
        proxy["name"] = name;
        return FilterNull(proxy);
    }

    private static Dictionary<string, object?> BuildVmessProxy(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        var proxy = BaseProxy(node, "vmess");
        proxy["uuid"] = node.Password;
        proxy["alterId"] = int.TryParse(extra.AlterId, out var alterId) ? alterId : 0;
        proxy["cipher"] = extra.VmessSecurity.NullIfEmpty() ?? Global.DefaultSecurity;
        AddTransport(proxy, node);
        AddTls(proxy, node);
        return proxy;
    }

    private static Dictionary<string, object?> BuildVlessProxy(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        var proxy = BaseProxy(node, "vless");
        proxy["uuid"] = node.Password;
        proxy["flow"] = extra.Flow.NullIfEmpty();
        AddTransport(proxy, node);
        AddTls(proxy, node);
        return proxy;
    }

    private static Dictionary<string, object?> BuildShadowsocksProxy(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        var proxy = BaseProxy(node, "ss");
        proxy["cipher"] = extra.SsMethod;
        proxy["password"] = node.Password;
        return proxy;
    }

    private static Dictionary<string, object?> BuildTrojanProxy(ProfileItem node)
    {
        var proxy = BaseProxy(node, "trojan");
        proxy["password"] = node.Password;
        AddTransport(proxy, node);
        AddTls(proxy, node, defaultTls: true);
        return proxy;
    }

    private static Dictionary<string, object?> BuildHysteria2Proxy(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        var proxy = BaseProxy(node, "hysteria2");
        proxy["password"] = node.Password;
        proxy["sni"] = node.Sni.NullIfEmpty();
        proxy["skip-cert-verify"] = node.GetAllowInsecure();
        proxy["obfs"] = extra.SalamanderPass.IsNotEmpty() ? "salamander" : null;
        proxy["obfs-password"] = extra.SalamanderPass.NullIfEmpty();
        proxy["up"] = extra.UpMbps > 0 ? extra.UpMbps : null;
        proxy["down"] = extra.DownMbps > 0 ? extra.DownMbps : null;
        proxy["ports"] = extra.Ports.NullIfEmpty();
        return proxy;
    }

    private static Dictionary<string, object?> BuildWireGuardProxy(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        var proxy = BaseProxy(node, "wireguard");
        proxy["private-key"] = node.Password;
        proxy["public-key"] = extra.WgPublicKey;
        proxy["preshared-key"] = extra.WgPresharedKey.NullIfEmpty();
        proxy["reserved"] = Utils.String2List(extra.WgReserved)?.Select(int.Parse).ToList();
        proxy["ip"] = Utils.String2List(extra.WgInterfaceAddress)?.FirstOrDefault();
        proxy["mtu"] = extra.WgMtu > 0 ? extra.WgMtu : null;
        return proxy;
    }

    private static Dictionary<string, object?> BuildSocksProxy(ProfileItem node)
    {
        var proxy = BaseProxy(node, "socks5");
        proxy["username"] = node.Username.NullIfEmpty();
        proxy["password"] = node.Password.NullIfEmpty();
        return proxy;
    }

    private static Dictionary<string, object?> BuildHttpProxy(ProfileItem node)
    {
        var proxy = BaseProxy(node, "http");
        proxy["username"] = node.Username.NullIfEmpty();
        proxy["password"] = node.Password.NullIfEmpty();
        AddTls(proxy, node);
        return proxy;
    }

    private static Dictionary<string, object?> BuildMieruProxy(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        var proxy = BaseProxy(node, "mieru");
        proxy["username"] = node.Username.NullIfEmpty();
        proxy["password"] = node.Password;
        proxy["transport"] = extra.MieruTransport.NullIfEmpty() ?? "TCP";
        proxy["multiplexing"] = extra.MieruMultiplexing.NullIfEmpty();
        proxy["port-range"] = extra.MieruPortRange.NullIfEmpty();
        proxy["traffic-pattern"] = extra.MieruTrafficPattern.NullIfEmpty();
        proxy["sni"] = node.Sni.NullIfEmpty();
        proxy["skip-cert-verify"] = node.GetAllowInsecure();
        return proxy;
    }

    private static Dictionary<string, object?> BaseProxy(ProfileItem node, string type)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = type,
            ["server"] = node.Address,
            ["port"] = node.Port,
        };
    }

    private static void AddTls(Dictionary<string, object?> proxy, ProfileItem node, bool defaultTls = false)
    {
        var enableTls = defaultTls || node.StreamSecurity is Global.StreamSecurity or Global.StreamSecurityReality;
        if (!enableTls)
        {
            return;
        }
        proxy["tls"] = true;
        proxy["skip-cert-verify"] = node.GetAllowInsecure();
        proxy["servername"] = node.Sni.NullIfEmpty();
        proxy["alpn"] = node.GetAlpn();
        proxy["client-fingerprint"] = node.Fingerprint.NullIfEmpty();
        if (node.StreamSecurity == Global.StreamSecurityReality)
        {
            proxy["reality-opts"] = FilterNull(new Dictionary<string, object?>
            {
                ["public-key"] = node.PublicKey,
                ["short-id"] = node.ShortId.NullIfEmpty(),
            });
        }
    }

    private static void AddTransport(Dictionary<string, object?> proxy, ProfileItem node)
    {
        var network = node.GetNetwork();
        var transport = node.GetTransportExtra();
        switch (network)
        {
            case nameof(ETransport.ws):
                proxy["network"] = "ws";
                proxy["ws-opts"] = FilterNull(new Dictionary<string, object?>
                {
                    ["path"] = transport.Path.NullIfEmpty(),
                    ["headers"] = transport.Host.IsNotEmpty()
                        ? new Dictionary<string, object?> { ["Host"] = transport.Host }
                        : null,
                });
                break;

            case nameof(ETransport.grpc):
                proxy["network"] = "grpc";
                proxy["grpc-opts"] = FilterNull(new Dictionary<string, object?>
                {
                    ["grpc-service-name"] = transport.GrpcServiceName.NullIfEmpty(),
                });
                break;

            case nameof(ETransport.httpupgrade):
                proxy["network"] = "httpupgrade";
                proxy["httpupgrade-opts"] = FilterNull(new Dictionary<string, object?>
                {
                    ["path"] = transport.Path.NullIfEmpty(),
                    ["host"] = transport.Host.NullIfEmpty(),
                });
                break;
        }
    }

    private static Dictionary<string, object?> FilterNull(Dictionary<string, object?> source)
    {
        return source
            .Where(kv => kv.Value is not null && (kv.Value is not string s || s.IsNotEmpty()))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static int NextFreePort(int start, HashSet<int> usedPorts)
    {
        for (var port = Math.Max(1, start); port < Global.MaxPort; port++)
        {
            if (usedPorts.Contains(port))
            {
                continue;
            }
            var freePort = Utils.GetFreePort(port);
            if (freePort == port && !usedPorts.Contains(freePort))
            {
                return freePort;
            }
            if (!usedPorts.Contains(freePort))
            {
                return freePort;
            }
        }
        return Utils.GetFreePort(start);
    }

    private static string SafeProxyName(ProfileItem node, string fallback)
    {
        return node.Remarks.IsNotEmpty() ? node.Remarks : fallback;
    }

    private static string GetLogLevel(string level)
    {
        return level == "none" ? "silent" : level;
    }
}
