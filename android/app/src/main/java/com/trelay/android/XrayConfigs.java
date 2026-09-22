package com.trelay.android;

import java.nio.charset.StandardCharsets;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import org.json.JSONArray;
import org.json.JSONObject;

// Port of Windows ShareLinkConverter + EmbeddedXrayConfig plus the Android front (TUN) config.
final class XrayConfigs {
    static final String PREFIX = "xrayjson://";
    private static final int CONN_IDLE = 86400;

    private XrayConfigs() { }

    // ------------------------------------------------------------------ backend (one server)

    /** Xray config with a loopback SOCKS inbound for an already resolved, non-dynamic link. */
    static JSONObject backend(String key, int socksPort) throws Exception {
        key = key == null ? "" : key.trim();
        if (socksPort < 1025 || socksPort > 65535) throw new IllegalArgumentException("Локальный SOCKS-порт должен быть от 1025 до 65535.");
        Links.validate(key);
        JSONObject root;
        if (key.startsWith(PREFIX)) root = prepareEmbedded(key, socksPort);
        else {
            JSONObject outbound;
            String scheme = Links.scheme(key);
            if (scheme.equals("vmess")) outbound = vmess(key);
            else {
                Links.Parsed link = Links.parse(key);
                switch (scheme) {
                    case "vless": outbound = vless(link); break;
                    case "trojan": outbound = trojan(link); break;
                    case "ss": outbound = shadowsocks(link); break;
                    case "socks": case "socks5": outbound = socks(link); break;
                    default: throw new IllegalArgumentException("Этот тип ключа запускается не через Xray.");
                }
            }
            outbound.put("tag", "proxy");
            root = new JSONObject()
                    .put("inbounds", new JSONArray().put(socksInbound(socksPort, "trelay-socks")))
                    .put("outbounds", new JSONArray().put(outbound));
        }
        root.put("log", new JSONObject().put("loglevel", "warning"));
        ensureLongIdle(root);
        return root;
    }

    static JSONObject socksInbound(int port, String tag) throws Exception {
        return new JSONObject().put("tag", tag).put("listen", "127.0.0.1").put("port", port).put("protocol", "socks")
                .put("settings", new JSONObject().put("udp", true).put("ip", "127.0.0.1").put("auth", "noauth"));
    }

    private static JSONObject vless(Links.Parsed link) throws Exception {
        JSONObject user = new JSONObject().put("id", link.user()).put("encryption", link.q("encryption", "none"));
        if (!link.q("flow").isEmpty()) user.put("flow", link.q("flow"));
        return new JSONObject().put("protocol", "vless")
                .put("settings", new JSONObject().put("vnext", new JSONArray().put(new JSONObject()
                        .put("address", link.host).put("port", link.port).put("users", new JSONArray().put(user)))))
                .put("streamSettings", stream(link, link.host));
    }

    private static JSONObject trojan(Links.Parsed link) throws Exception {
        if (link.q("security").isEmpty()) link.query.put("security", "tls");
        return new JSONObject().put("protocol", "trojan")
                .put("settings", new JSONObject().put("servers", new JSONArray().put(new JSONObject()
                        .put("address", link.host).put("port", link.port).put("password", link.user()))))
                .put("streamSettings", stream(link, link.host));
    }

    private static JSONObject shadowsocks(Links.Parsed link) throws Exception {
        String credentials = Links.shadowsocksCredentials(link.rawUser);
        int split = credentials.indexOf(':');
        if (split < 1) throw new IllegalArgumentException("Некорректный Shadowsocks-ключ.");
        return new JSONObject().put("protocol", "shadowsocks")
                .put("settings", new JSONObject().put("servers", new JSONArray().put(new JSONObject()
                        .put("address", link.host).put("port", link.port)
                        .put("method", credentials.substring(0, split)).put("password", credentials.substring(split + 1)))));
    }

    private static JSONObject socks(Links.Parsed link) throws Exception {
        JSONObject server = new JSONObject().put("address", link.host).put("port", link.port);
        String credentials = link.user();
        if (!credentials.isEmpty()) {
            if (credentials.indexOf(':') < 0) {
                try { credentials = Links.strictUtf8(Links.base64(credentials)); } catch (Exception ignored) { }
            }
            int split = credentials.indexOf(':');
            server.put("users", new JSONArray().put(new JSONObject()
                    .put("user", credentials.substring(0, split)).put("pass", credentials.substring(split + 1))));
        }
        return new JSONObject().put("protocol", "socks").put("settings", new JSONObject().put("servers", new JSONArray().put(server)));
    }

    private static JSONObject vmess(String key) throws Exception {
        JSONObject v = new JSONObject(Links.strictUtf8(Links.base64(key.substring("vmess://".length()))));
        JSONObject user = new JSONObject().put("id", v.optString("id")).put("alterId", v.optInt("aid", 0))
                .put("security", Links.empty(v.optString("scy"), "auto"));
        Links.Parsed q = new Links.Parsed();
        q.query.put("type", Links.empty(v.optString("net"), "tcp"));
        q.query.put("security", v.optString("tls"));
        q.query.put("sni", v.optString("sni"));
        q.query.put("host", v.optString("host"));
        q.query.put("path", v.optString("path"));
        q.query.put("fp", v.optString("fp"));
        q.query.put("alpn", v.optString("alpn"));
        q.query.put("headerType", v.optString("type"));
        if ("grpc".equals(q.q("type"))) q.query.put("serviceName", v.optString("path"));
        String address = v.optString("add");
        int port = Integer.parseInt(v.optString("port").trim());
        return new JSONObject().put("protocol", "vmess")
                .put("settings", new JSONObject().put("vnext", new JSONArray().put(new JSONObject()
                        .put("address", address).put("port", port).put("users", new JSONArray().put(user)))))
                .put("streamSettings", stream(q, address));
    }

    private static JSONObject stream(Links.Parsed q, String fallbackServer) throws Exception {
        String network = Links.empty(q.q("type"), "tcp").toLowerCase(Locale.ROOT);
        if (network.equals("splithttp")) network = "xhttp";
        String security = Links.empty(q.q("security"), "none").toLowerCase(Locale.ROOT);
        JSONObject stream = new JSONObject().put("network", network).put("security", security);
        String serverName = Links.empty(q.q("sni"), fallbackServer);
        String fingerprint = Links.empty(q.q("fp"), "chrome");
        if (security.equals("tls")) {
            JSONObject tls = new JSONObject().put("serverName", serverName).put("fingerprint", fingerprint);
            if (!q.q("alpn").isEmpty()) tls.put("alpn", new JSONArray(q.q("alpn").split(",")));
            stream.put("tlsSettings", tls);
        }
        if (security.equals("reality")) stream.put("realitySettings", new JSONObject().put("serverName", serverName)
                .put("fingerprint", fingerprint).put("publicKey", q.q("pbk")).put("shortId", q.q("sid"))
                .put("spiderX", q.q("spx")));
        String host = Links.empty(q.q("host"), serverName);
        switch (network) {
            case "ws":
                stream.put("wsSettings", new JSONObject().put("path", Links.empty(q.q("path"), "/")).put("host", host));
                break;
            case "grpc": {
                JSONObject grpc = new JSONObject().put("serviceName", q.q("serviceName")).put("multiMode", "multi".equals(q.q("mode")));
                if (!q.q("authority").isEmpty()) grpc.put("authority", q.q("authority"));
                stream.put("grpcSettings", grpc);
                break;
            }
            case "httpupgrade":
                stream.put("httpupgradeSettings", new JSONObject().put("path", Links.empty(q.q("path"), "/")).put("host", host));
                break;
            case "xhttp": {
                JSONObject xhttp = new JSONObject().put("path", Links.empty(q.q("path"), "/")).put("host", host)
                        .put("mode", Links.empty(q.q("mode"), "auto"));
                if (!q.q("extra").isEmpty()) {
                    try { xhttp.put("extra", new JSONObject(q.q("extra"))); } catch (Exception ignored) { }
                }
                stream.put("xhttpSettings", xhttp);
                break;
            }
            default:
                if ("http".equals(q.q("headerType"))) stream.put("tcpSettings", new JSONObject().put("header", new JSONObject()
                        .put("type", "http").put("request", new JSONObject()
                                .put("path", new JSONArray().put(Links.empty(q.q("path"), "/")))
                                .put("headers", new JSONObject().put("Host", new JSONArray().put(host))))));
        }
        return stream;
    }

    // Xray closes an idle proxied connection after 300 s. Telephony signalling can stay silent longer
    // (Windows 0.2.1: regular 15-30 minute drops). Idle limits are raised, never lowered.
    static void ensureLongIdle(JSONObject root) throws Exception {
        JSONObject policy = root.optJSONObject("policy");
        if (policy == null) { policy = new JSONObject(); root.put("policy", policy); }
        JSONObject levels = policy.optJSONObject("levels");
        if (levels == null) { levels = new JSONObject(); policy.put("levels", levels); }
        if (!levels.has("0")) levels.put("0", new JSONObject());
        Iterator<String> keys = levels.keys();
        while (keys.hasNext()) {
            JSONObject level = levels.optJSONObject(keys.next());
            if (level != null && level.optInt("connIdle", 0) < CONN_IDLE) level.put("connIdle", CONN_IDLE);
        }
    }

    // ------------------------------------------------------------------ embedded full Xray configs

    static String encodeEmbedded(JSONObject root) {
        validateEmbedded(root);
        String encoded = PREFIX + Links.base64Url(root.toString().getBytes(StandardCharsets.UTF_8));
        if (encoded.length() > Links.MAX_KEY_LENGTH) throw new IllegalArgumentException("Xray-конфиг превышает допустимый размер.");
        return encoded;
    }

    static JSONObject decodeEmbedded(String value) {
        try {
            JSONObject root = new JSONObject(Links.strictUtf8(Links.base64(value.trim().substring(PREFIX.length()))));
            validateEmbedded(root);
            return root;
        } catch (IllegalArgumentException error) { throw error; }
        catch (Exception error) { throw new IllegalArgumentException("Встроенный Xray-конфиг повреждён."); }
    }

    static boolean isEmbeddedCandidate(JSONObject root) {
        try { validateEmbedded(root); return true; } catch (Exception error) { return false; }
    }

    static void validateEmbedded(JSONObject root) {
        JSONArray outbounds = root == null ? null : root.optJSONArray("outbounds");
        if (outbounds == null || outbounds.length() == 0) throw new IllegalArgumentException("Во встроенном Xray-конфиге нет выходного подключения.");
        boolean remote = false;
        for (int i = 0; i < outbounds.length(); i++) {
            JSONObject outbound = outbounds.optJSONObject(i);
            String protocol = outbound == null ? "" : outbound.optString("protocol").trim();
            if (protocol.isEmpty() || protocol.length() > 40 || !protocol.matches("[A-Za-z0-9_-]+"))
                throw new IllegalArgumentException("Во встроенном Xray-конфиге повреждён список выходных подключений.");
            String lower = protocol.toLowerCase(Locale.ROOT);
            if (!(lower.equals("freedom") || lower.equals("blackhole") || lower.equals("dns") || lower.equals("loopback"))) remote = true;
        }
        if (!remote) throw new IllegalArgumentException("Во встроенном Xray-конфиге нет VPN-подключения.");
    }

    static String embeddedName(String value) {
        try { return Links.cleanName(decodeEmbedded(value).optString("remarks")); } catch (Exception error) { return ""; }
    }

    static String embeddedEndpoint(String value) {
        try {
            JSONArray outbounds = decodeEmbedded(value).getJSONArray("outbounds");
            for (int i = 0; i < outbounds.length(); i++) {
                JSONObject settings = outbounds.getJSONObject(i).optJSONObject("settings");
                if (settings == null) continue;
                for (String key : new String[] {"vnext", "servers"}) {
                    JSONArray servers = settings.optJSONArray(key);
                    JSONObject server = servers == null ? null : servers.optJSONObject(0);
                    if (server != null && !server.optString("address").isEmpty() && server.optInt("port", 0) > 0)
                        return server.optString("address") + ":" + server.optInt("port");
                }
                if (!settings.optString("address").isEmpty() && settings.optInt("port", 0) > 0)
                    return settings.optString("address") + ":" + settings.optInt("port");
            }
        } catch (Exception ignored) { }
        return "конфигурация Xray";
    }

    private static JSONObject prepareEmbedded(String value, int socksPort) throws Exception {
        JSONObject source = decodeEmbedded(value);
        JSONObject result = new JSONObject();
        for (String key : new String[] {"dns", "routing", "outbounds", "policy", "transport", "observatory", "burstObservatory", "fakedns", "fakeDns"})
            if (source.has(key)) result.put(key, source.get(key));
        String tag = "trelay-socks";
        JSONArray inbounds = source.optJSONArray("inbounds");
        JSONObject first = inbounds == null ? null : inbounds.optJSONObject(0);
        String oldTag = first == null ? "" : first.optString("tag");
        if (!oldTag.trim().isEmpty() && oldTag.length() <= 64 && !oldTag.matches(".*\\p{Cntrl}.*")) tag = oldTag;
        JSONObject inbound = socksInbound(socksPort, tag);
        if (first != null && first.optJSONObject("sniffing") != null) inbound.put("sniffing", first.getJSONObject("sniffing"));
        result.put("inbounds", new JSONArray().put(inbound));
        // A provider config must never open its own API listener on the phone.
        JSONObject api = source.optJSONObject("api");
        if (api != null) { api.remove("listen"); result.put("api", api); }
        stripGeo(result);
        return result;
    }

    /**
     * Removes everything that would make Xray open geoip.dat or geosite.dat.
     *
     * Provider configurations carry their own routing: send Russian sites straight out, block ads,
     * and so on. Those rules name whole countries and site lists, which Xray can only resolve from
     * geo database files of several megabytes that are not on the phone — it then refuses the whole
     * configuration with "stat …/geoip.dat: no such file or directory" and the server never starts.
     * Carrying the databases would add ~12 MB to the application for rules TRELAY does not want in
     * the first place: a route exists precisely so that its applications go through the server, not
     * around it. So the geo selectors are dropped, and a rule that had nothing else to match on goes
     * with them; what is left routes by port, network and address exactly as the provider intended.
     */
    private static void stripGeo(JSONObject root) {
        int removed = 0;
        JSONObject routing = root.optJSONObject("routing");
        JSONArray rules = routing == null ? null : routing.optJSONArray("rules");
        if (rules != null) {
            JSONArray kept = new JSONArray();
            for (int i = 0; i < rules.length(); i++) {
                JSONObject rule = rules.optJSONObject(i);
                if (rule == null) continue;
                boolean hadDomain = rule.has("domain"), hadIp = rule.has("ip");
                boolean touched = withoutGeo(rule, "domain") | withoutGeo(rule, "ip");
                if (!touched) { kept.put(rule); continue; }
                removed++;
                // A rule that only said "these countries" or "these site lists" has nothing left to
                // match; keeping it would send every connection down that outbound.
                boolean selects = (hadDomain && rule.has("domain")) || (hadIp && rule.has("ip"));
                for (String key : new String[] {"port", "sourcePort", "network", "source", "user", "inboundTag", "protocol", "attrs", "process", "domainRegex"})
                    if (rule.has(key)) selects = true;
                if (selects) kept.put(rule);
            }
            if (kept.length() == 0) root.remove("routing");
            else try { routing.put("rules", kept); } catch (Exception ignored) { }
        }
        JSONObject dns = root.optJSONObject("dns");
        JSONArray servers = dns == null ? null : dns.optJSONArray("servers");
        if (servers != null) {
            JSONArray kept = new JSONArray();
            for (int i = 0; i < servers.length(); i++) {
                JSONObject server = servers.optJSONObject(i);
                if (server == null) { kept.put(servers.opt(i)); continue; }
                boolean hadDomains = server.has("domains");
                boolean touched = withoutGeo(server, "domains") | withoutGeo(server, "expectIPs") | withoutGeo(server, "expectedIPs");
                if (touched) removed++;
                // A resolver that was only for a geo list has no domains left to answer for.
                if (!hadDomains || server.has("domains")) kept.put(server);
            }
            if (kept.length() == 0) root.remove("dns");
            else try { dns.put("servers", kept); } catch (Exception ignored) { }
        }
        JSONObject hosts = dns == null ? null : dns.optJSONObject("hosts");
        if (hosts != null) {
            for (Iterator<String> keys = hosts.keys(); keys.hasNext(); ) {
                String key = keys.next().toLowerCase(Locale.ROOT);
                if (key.startsWith("geosite:") || key.startsWith("ext:")) { keys.remove(); removed++; }
            }
        }
        // Anything left that still names a geo list — a balancer, a nested rule shape this version of
        // Xray understands and TRELAY does not — would fail the same way, so the whole section goes.
        if (namesGeo(root.opt("routing"))) { root.remove("routing"); removed++; }
        if (namesGeo(root.opt("dns"))) { root.remove("dns"); removed++; }
        if (removed > 0) EngineLog.write("CONFIG_GEO_STRIPPED entries=" + removed);
    }

    /** True when a section still refers to a geo database, in any shape. */
    private static boolean namesGeo(Object section) {
        if (section == null) return false;
        String text = String.valueOf(section).toLowerCase(Locale.ROOT);
        return text.contains("geosite:") || text.contains("geoip:") || text.contains("\"ext:") || text.contains("ext-ip:");
    }

    /** Drops geo selectors from one array field; true when something was dropped. */
    private static boolean withoutGeo(JSONObject holder, String field) {
        JSONArray values = holder.optJSONArray(field);
        if (values == null) return false;
        JSONArray kept = new JSONArray();
        boolean dropped = false;
        for (int i = 0; i < values.length(); i++) {
            String value = values.optString(i, "");
            String lower = value.toLowerCase(Locale.ROOT);
            if (lower.startsWith("geosite:") || lower.startsWith("geoip:") || lower.startsWith("ext:") || lower.startsWith("ext-ip:")) dropped = true;
            else kept.put(values.opt(i));
        }
        if (!dropped) return false;
        try {
            if (kept.length() == 0) holder.remove(field);
            else holder.put(field, kept);
        } catch (Exception ignored) { }
        return true;
    }

    // ------------------------------------------------------------------ Android front: TUN -> per-app groups

    static final class FrontGroup {
        final String tag;
        final List<Integer> uids;
        final int relayPort;
        FrontGroup(String tag, List<Integer> uids, int relayPort) { this.tag = tag; this.uids = uids; this.relayPort = relayPort; }
    }

    static final String TUN_ADDRESS = "10.111.222.1";
    static final String TUN_DNS = "10.111.222.2";
    static final String TUN_ADDRESS6 = "fd66:7472:6c79::1";

    /** Front instance: reads the VPN TUN, maps each connection to its application's route relay. */
    static JSONObject front(List<FrontGroup> groups, int dnsPort, int mtu) throws Exception {
        JSONArray outbounds = new JSONArray()
                .put(new JSONObject().put("tag", "block").put("protocol", "blackhole"))
                .put(new JSONObject().put("tag", "dns-local").put("protocol", "freedom")
                        .put("settings", new JSONObject().put("redirect", "127.0.0.1:" + dnsPort)))
                .put(new JSONObject().put("tag", "lan").put("protocol", "freedom"));
        JSONArray rules = new JSONArray()
                .put(new JSONObject().put("type", "field").put("port", "53").put("outboundTag", "dns-local"))
                .put(new JSONObject().put("type", "field").put("ip", new JSONArray()
                        .put("10.0.0.0/8").put("172.16.0.0/12").put("192.168.0.0/16").put("169.254.0.0/16")
                        .put("100.64.0.0/10").put("fc00::/7").put("fe80::/10")).put("outboundTag", "lan"));
        for (FrontGroup group : groups) {
            outbounds.put(new JSONObject().put("tag", group.tag).put("protocol", "socks")
                    .put("settings", new JSONObject().put("servers", new JSONArray().put(new JSONObject()
                            .put("address", "127.0.0.1").put("port", group.relayPort)))));
            if (groups.size() > 1 && !group.uids.isEmpty()) {
                JSONArray names = new JSONArray();
                for (Integer uid : group.uids) names.put(String.valueOf(uid));
                rules.put(new JSONObject().put("type", "field").put("process", names).put("outboundTag", group.tag));
            }
        }
        // Only the selected applications are inside the VPN. With one route everything belongs to it;
        // with several, a connection whose owner cannot be proven is blocked rather than guessed.
        String fallback = groups.size() == 1 ? groups.get(0).tag : "block";
        rules.put(new JSONObject().put("type", "field").put("network", "tcp,udp").put("outboundTag", fallback));
        JSONObject root = new JSONObject()
                .put("log", new JSONObject().put("loglevel", "warning"))
                .put("inbounds", new JSONArray().put(new JSONObject().put("tag", "tun").put("port", 0).put("protocol", "tun")
                        .put("settings", new JSONObject().put("name", "trelay0").put("mtu", mtu))))
                .put("outbounds", outbounds)
                .put("routing", new JSONObject().put("domainStrategy", "AsIs").put("rules", rules));
        ensureLongIdle(root);
        return root;
    }

    /** Short-lived local HTTP proxy that fragments the TLS ClientHello (subscription fallback). */
    static JSONObject fragmentProxy(int port) throws Exception {
        return new JSONObject()
                .put("log", new JSONObject().put("loglevel", "warning"))
                .put("inbounds", new JSONArray().put(new JSONObject().put("tag", "subscription-http").put("listen", "127.0.0.1")
                        .put("port", port).put("protocol", "http").put("settings", new JSONObject().put("allowTransparent", false))))
                .put("outbounds", new JSONArray().put(new JSONObject().put("tag", "fragment").put("protocol", "freedom")
                        .put("settings", new JSONObject().put("domainStrategy", "UseIPv4")
                                .put("fragment", new JSONObject().put("packets", "tlshello").put("length", "10-20").put("interval", "1-3")))
                        .put("streamSettings", new JSONObject().put("sockopt", new JSONObject().put("tcpNoDelay", true)))));
    }
}
