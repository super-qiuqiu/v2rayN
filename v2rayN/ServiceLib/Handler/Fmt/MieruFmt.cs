namespace ServiceLib.Handler.Fmt;

public class MieruFmt : BaseFmt
{
    public static ProfileItem? Resolve(string str, out string msg)
    {
        msg = ResUI.ConfigurationFormatIncorrect;

        var parsedUrl = Utils.TryUri(str);
        if (parsedUrl == null)
        {
            return null;
        }

        ProfileItem item = new()
        {
            ConfigType = EConfigType.Mieru,
            Remarks = parsedUrl.GetComponents(UriComponents.Fragment, UriFormat.Unescaped),
            Address = parsedUrl.IdnHost,
            Port = parsedUrl.Port,
            CoreType = ECoreType.mihomo,
        };

        var rawUserInfo = Utils.UrlDecode(parsedUrl.UserInfo);
        if (rawUserInfo.Contains(':'))
        {
            var split = rawUserInfo.Split(':', 2);
            item.Username = split[0];
            item.Password = split[1];
        }
        else
        {
            item.Password = rawUserInfo;
        }

        var query = Utils.ParseQueryString(parsedUrl.Query);

        var protocolExtra = item.GetProtocolExtra();

        // transport: TCP or UDP
        var transport = GetQueryValue(query, "transport");
        if (transport.IsNotEmpty())
        {
            protocolExtra = protocolExtra with
            {
                MieruTransport = transport.ToUpperInvariant(),
            };
        }

        // multiplexing: MULTIPLEXING_OFF/LOW/MIDDLE/HIGH
        var multiplexing = GetQueryValue(query, "multiplexing");
        if (multiplexing.IsNotEmpty())
        {
            protocolExtra = protocolExtra with
            {
                MieruMultiplexing = multiplexing,
            };
        }

        // port-range: e.g. "2090-2099"
        var portRange = GetQueryDecoded(query, "port-range");
        if (portRange.IsNotEmpty())
        {
            protocolExtra = protocolExtra with
            {
                MieruPortRange = portRange,
            };
        }

        // traffic-pattern: base64 string
        var trafficPattern = GetQueryDecoded(query, "traffic-pattern");
        if (trafficPattern.IsNotEmpty())
        {
            protocolExtra = protocolExtra with
            {
                MieruTrafficPattern = trafficPattern,
            };
        }

        item.SetProtocolExtra(protocolExtra);

        // TLS fields
        ResolveUriQuery(query, ref item);

        if (item.StreamSecurity.IsNullOrEmpty())
        {
            item.StreamSecurity = Global.StreamSecurity;
        }

        return item;
    }

    public static string? ToUri(ProfileItem? item)
    {
        if (item == null)
        {
            return null;
        }
        var remark = string.Empty;
        if (item.Remarks.IsNotEmpty())
        {
            remark = "#" + Utils.UrlEncode(item.Remarks);
        }

        var userInfo = item.Username.IsNotEmpty()
            ? $"{Utils.UrlEncode(item.Username)}:{Utils.UrlEncode(item.Password)}"
            : Utils.UrlEncode(item.Password);

        var dicQuery = new Dictionary<string, string>();
        ToUriQueryLite(item, ref dicQuery);

        var protocolExtra = item.GetProtocolExtra();

        if (protocolExtra.MieruTransport.IsNotEmpty())
        {
            dicQuery.Add("transport", protocolExtra.MieruTransport);
        }
        if (protocolExtra.MieruMultiplexing.IsNotEmpty())
        {
            dicQuery.Add("multiplexing", protocolExtra.MieruMultiplexing);
        }
        if (protocolExtra.MieruPortRange.IsNotEmpty())
        {
            dicQuery.Add("port-range", Utils.UrlEncode(protocolExtra.MieruPortRange));
        }
        if (protocolExtra.MieruTrafficPattern.IsNotEmpty())
        {
            dicQuery.Add("traffic-pattern", Utils.UrlEncode(protocolExtra.MieruTrafficPattern));
        }

        var query = dicQuery.Count > 0
            ? ("?" + string.Join("&", dicQuery.Select(x => x.Key + "=" + x.Value).ToArray()))
            : string.Empty;

        return $"{Global.ProtocolShares[EConfigType.Mieru]}{userInfo}@{GetIpv6(item.Address)}:{item.Port}{query}{remark}";
    }
}
