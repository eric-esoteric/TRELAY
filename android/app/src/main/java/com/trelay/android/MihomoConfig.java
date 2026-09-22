package com.trelay.android;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import org.json.JSONArray;
import org.json.JSONObject;
import org.yaml.snakeyaml.LoaderOptions;
import org.yaml.snakeyaml.Yaml;
import org.yaml.snakeyaml.constructor.SafeConstructor;

// Port of Windows MihomoConfig. Subscription YAML/JSON is never executed verbatim: only the proxy
// definitions needed by one node are kept and TRELAY supplies its own loopback listener and rules.
final class MihomoConfig {
    static final String PREFIX = "trelay-mihomo://";
    private static final int MAX_NODES = 128;

    private MihomoConfig() { }

    static boolean isDirectLink(String value) {
        String scheme = Links.scheme(value);
        if (scheme.equals("ss")) {
            String payload = value.trim().substring("ss://".length());
            int end = indexOfAny(payload, '?', '#');
            String main = end >= 0 ? payload.substring(0, end) : payload;
            if (main.indexOf('@') < 0) return true;
            try { return !Links.parse(value).q("plugin").trim().isEmpty(); } catch (Exception error) { return false; }
        }
        return scheme.equals("hysteria") || scheme.equals("hysteria2") || scheme.equals("hy2") || scheme.equals("tuic") ||
                scheme.equals("ssr") || scheme.equals("wireguard") || scheme.equals("wg") || scheme.equals("anytls");
    }

    private static int indexOfAny(String text, char a, char b) {
        int x = text.indexOf(a), y = text.indexOf(b);
        if (x < 0) return y;
        if (y < 0) return x;
        return Math.min(x, y);
    }

    // ------------------------------------------------------------------ Clash / sing-box / SIP008

    static Object parseYaml(String text) {
        LoaderOptions options = new LoaderOptions();
        options.setMaxAliasesForCollections(64);
        options.setCodePointLimit(4 * 1024 * 1024);
        return new Yaml(new SafeConstructor(options)).load(text);
    }

    static List<String> extractClash(String text) {
        List<String> links = new ArrayList<>();
        if (text == null || text.trim().isEmpty() || text.length() > 2 * 1024 * 1024) return links;
        try {
            Object parsed = parseYaml(text);
            if (!(parsed instanceof Map)) return links;
            JSONObject root = new JSONObject((Map<?, ?>) parsed);
            JSONArray proxies = root.optJSONArray("proxies");
            if (proxies == null || proxies.length() == 0) return links;
            JSONArray groups = root.optJSONArray("proxy-groups");
            Map<String, JSONObject> proxyMap = namedMap(proxies);
            Map<String, JSONObject> groupMap = namedMap(groups == null ? new JSONArray() : groups);
            for (int i = 0; i < Math.min(MAX_NODES, proxies.length()); i++) {
                JSONObject proxy = proxies.optJSONObject(i);
                if (!validProxy(proxy)) continue;
                String selected = proxy.optString("name");
                Map<String, JSONObject> requiredProxies = new LinkedHashMap<>();
                Map<String, JSONObject> requiredGroups = new LinkedHashMap<>();
                collectDependency(selected, proxyMap, groupMap, requiredProxies, requiredGroups, new HashSet<>(), 0);
                if (!requiredProxies.containsKey(selected)) requiredProxies.put(selected, proxy);
                try { links.add(encodePack(selected, new ArrayList<>(requiredProxies.values()), new ArrayList<>(requiredGroups.values()))); }
                catch (IllegalStateException ignored) { }
            }
        } catch (Throwable error) { links.clear(); }
        return links;
    }

    static List<String> extractClashProviderUrls(String text) {
        Set<String> urls = new LinkedHashSet<>();
        try {
            Object parsed = parseYaml(text);
            if (!(parsed instanceof Map)) return new ArrayList<>(urls);
            JSONObject providers = new JSONObject((Map<?, ?>) parsed).optJSONObject("proxy-providers");
            if (providers == null) return new ArrayList<>(urls);
            Iterator<String> keys = providers.keys();
            int count = 0;
            while (keys.hasNext() && count++ < 16) {
                JSONObject provider = providers.optJSONObject(keys.next());
                if (provider == null || !"http".equalsIgnoreCase(provider.optString("type"))) continue;
                String url = provider.optString("url");
                try { ShareLinkResolver.subscriptionUri(url); urls.add(url.trim()); } catch (Exception ignored) { }
            }
        } catch (Throwable ignored) { }
        return new ArrayList<>(urls);
    }

    static List<String> extractSingBox(Object parsed) {
        List<String> links = new ArrayList<>();
        JSONArray outbounds = parsed instanceof JSONObject ? ((JSONObject) parsed).optJSONArray("outbounds") : null;
        JSONArray candidates = outbounds != null ? outbounds : parsed instanceof JSONArray ? (JSONArray) parsed : null;
        if (candidates == null && parsed instanceof JSONObject) candidates = new JSONArray().put(parsed);
        if (candidates == null) return links;
        for (int i = 0; i < Math.min(MAX_NODES, candidates.length()); i++) {
            JSONObject source = candidates.optJSONObject(i);
            JSONObject proxy = mapSingBox(source);
            if (proxy == null && outbounds == null && hasPlugin(source)) proxy = mapSip008(source);
            if (proxy == null) continue;
            try { links.add(encodePack(proxy.optString("name"), listOf(proxy), new ArrayList<>())); } catch (IllegalStateException ignored) { }
        }
        return links;
    }

    static List<String> extractSip008(Object parsed) {
        List<String> links = new ArrayList<>();
        JSONArray candidates = parsed instanceof JSONArray ? (JSONArray) parsed : null;
        if (parsed instanceof JSONObject) {
            JSONArray servers = ((JSONObject) parsed).optJSONArray("servers");
            candidates = servers != null ? servers : new JSONArray().put(parsed);
        }
        if (candidates == null) return links;
        for (int i = 0; i < Math.min(MAX_NODES, candidates.length()); i++) {
            JSONObject proxy = mapSip008(candidates.optJSONObject(i));
            if (proxy == null) continue;
            try { links.add(encodePack(proxy.optString("name"), listOf(proxy), new ArrayList<>())); } catch (IllegalStateException ignored) { }
        }
        return links;
    }

    private static List<JSONObject> listOf(JSONObject item) { List<JSONObject> list = new ArrayList<>(); list.add(item); return list; }

    // ------------------------------------------------------------------ pack

    static String encodeDirect(String value) {
        JSONObject proxy = mapDirect(value.trim());
        return encodePack(proxy.optString("name"), listOf(proxy), new ArrayList<>());
    }

    private static String encodePack(String selected, List<JSONObject> proxies, List<JSONObject> groups) {
        try {
            JSONObject pack = new JSONObject().put("selected", selected).put("proxies", new JSONArray(proxies)).put("groups", new JSONArray(groups));
            String result = PREFIX + Links.base64Url(pack.toString().getBytes(StandardCharsets.UTF_8));
            if (result.length() > Links.MAX_KEY_LENGTH) throw new IllegalStateException("Конфигурация одного сервера превышает 64 КБ.");
            return result;
        } catch (IllegalStateException error) { throw error; }
        catch (Exception error) { throw new IllegalStateException("Не удалось упаковать сервер."); }
    }

    static JSONObject decode(String value) {
        try {
            byte[] bytes = Links.base64(value.trim().substring(PREFIX.length()));
            if (bytes.length == 0 || bytes.length > 1024 * 1024) throw new IllegalArgumentException();
            JSONObject pack = new JSONObject(Links.strictUtf8(bytes));
            String selected = pack.getString("selected");
            JSONArray proxies = pack.getJSONArray("proxies");
            if (proxies.length() == 0 || proxies.length() > 32) throw new IllegalArgumentException();
            boolean found = false;
            for (int i = 0; i < proxies.length(); i++) {
                JSONObject proxy = proxies.getJSONObject(i);
                if (!validProxy(proxy)) throw new IllegalArgumentException();
                if (selected.equals(proxy.optString("name"))) found = true;
            }
            if (!found) throw new IllegalArgumentException();
            return pack;
        } catch (Exception error) { throw new IllegalArgumentException("Встроенная конфигурация Mihomo повреждена."); }
    }

    /** Mihomo accepts JSON documents as YAML, so no YAML emitter is needed. */
    static String prepareForSocks(String key, int port) throws Exception {
        if (isDirectLink(key)) key = encodeDirect(key);
        JSONObject pack = decode(key);
        JSONArray proxies = pack.getJSONArray("proxies");
        JSONArray groups = pack.optJSONArray("groups");
        if (groups == null) groups = new JSONArray();
        Set<String> used = new HashSet<>();
        for (int i = 0; i < proxies.length(); i++) used.add(proxies.getJSONObject(i).optString("name"));
        for (int i = 0; i < groups.length(); i++) used.add(groups.getJSONObject(i).optString("name"));
        String control = "TRELAY";
        for (int index = 2; used.contains(control); index++) control = "TRELAY-" + index;
        groups.put(new JSONObject().put("name", control).put("type", "select").put("proxies", new JSONArray().put(pack.getString("selected"))));
        JSONObject root = new JSONObject()
                .put("socks-port", port).put("allow-lan", false).put("bind-address", "127.0.0.1")
                .put("mode", "rule").put("log-level", "warning").put("ipv6", true)
                .put("keep-alive-idle", 600).put("keep-alive-interval", 30)
                .put("dns", new JSONObject().put("enable", true).put("ipv6", true)
                        .put("default-nameserver", new JSONArray().put("system").put("1.1.1.1").put("8.8.8.8"))
                        .put("nameserver", new JSONArray().put("system").put("1.1.1.1").put("8.8.8.8")))
                .put("proxies", proxies).put("proxy-groups", groups)
                .put("rules", new JSONArray().put("MATCH," + control));
        return root.toString(2);
    }

    static String name(String value) {
        try { return Links.cleanName(decode(value).getString("selected")); } catch (Exception error) { return ""; }
    }

    static String endpoint(String value) {
        try {
            JSONObject pack = decode(value);
            JSONArray proxies = pack.getJSONArray("proxies");
            for (int i = 0; i < proxies.length(); i++) {
                JSONObject proxy = proxies.getJSONObject(i);
                if (proxy.optString("name").equals(pack.getString("selected")))
                    return proxy.optString("server") + ":" + proxy.optString("port");
            }
        } catch (Exception ignored) { }
        return "сервер";
    }

    static String typeLabel(String value) {
        try {
            JSONObject pack = decode(value);
            JSONArray proxies = pack.getJSONArray("proxies");
            for (int i = 0; i < proxies.length(); i++) {
                JSONObject proxy = proxies.getJSONObject(i);
                if (proxy.optString("name").equals(pack.getString("selected"))) return proxy.optString("type").toUpperCase(Locale.ROOT);
            }
        } catch (Exception ignored) { }
        return "MIHOMO";
    }

    // ------------------------------------------------------------------ mapping

    private static Map<String, JSONObject> namedMap(JSONArray items) {
        Map<String, JSONObject> map = new HashMap<>();
        for (int i = 0; i < items.length(); i++) {
            JSONObject item = items.optJSONObject(i);
            String name = item == null ? "" : item.optString("name");
            if (!name.trim().isEmpty() && !map.containsKey(name)) map.put(name, item);
        }
        return map;
    }

    private static void collectDependency(String name, Map<String, JSONObject> proxies, Map<String, JSONObject> groups,
                                          Map<String, JSONObject> resultProxies, Map<String, JSONObject> resultGroups,
                                          Set<String> visiting, int depth) {
        if (depth > 12 || !visiting.add(name) || resultProxies.size() + resultGroups.size() > 31) return;
        JSONObject proxy = proxies.get(name);
        if (proxy != null) {
            resultProxies.put(name, proxy);
            String dialer = proxy.optString("dialer-proxy");
            if (!dialer.isEmpty()) collectDependency(dialer, proxies, groups, resultProxies, resultGroups, visiting, depth + 1);
        } else {
            JSONObject group = groups.get(name);
            if (group != null) {
                resultGroups.put(name, group);
                JSONArray members = group.optJSONArray("proxies");
                if (members != null) for (int i = 0; i < members.length(); i++) {
                    Object member = members.opt(i);
                    if (member instanceof String) collectDependency((String) member, proxies, groups, resultProxies, resultGroups, visiting, depth + 1);
                }
            }
        }
        visiting.remove(name);
    }

    private static JSONObject mapDirect(String value) {
        String scheme = Links.scheme(value);
        try {
            if (scheme.equals("ss")) return mapShadowsocks(value);
            if (scheme.equals("ssr")) return mapSsr(value);
            Links.Parsed uri = Links.parse(value);
            if (!Links.hasServer(uri)) throw new IllegalArgumentException("В ссылке нет сервера или порта.");
            String label = Links.cleanName(uri.fragment);
            JSONObject proxy = new JSONObject().put("name", label.isEmpty() ? uri.host : label).put("server", uri.host)
                    .put("port", uri.port).put("udp", true);
            switch (scheme) {
                case "hysteria": {
                    proxy.put("type", "hysteria");
                    if (!uri.user().trim().isEmpty()) proxy.put("auth-str", uri.user());
                    copy(uri, proxy, "auth-str", "auth", "auth_str");
                    copy(uri, proxy, "sni", "sni", "peer");
                    copy(uri, proxy, "protocol", "protocol");
                    copy(uri, proxy, "obfs", "obfs");
                    putInt(proxy, "up", uri.first("up", "upmbps"));
                    putInt(proxy, "down", uri.first("down", "downmbps"));
                    break;
                }
                case "hysteria2": case "hy2":
                    proxy.put("type", "hysteria2").put("password", uri.user());
                    copy(uri, proxy, "sni", "sni");
                    copy(uri, proxy, "obfs", "obfs");
                    copy(uri, proxy, "obfs-password", "obfs-password", "obfs_password");
                    copy(uri, proxy, "ports", "mport", "ports");
                    break;
                case "tuic": {
                    proxy.put("type", "tuic");
                    String user = uri.user();
                    int split = user.indexOf(':');
                    if (split > 0) proxy.put("uuid", user.substring(0, split)).put("password", user.substring(split + 1));
                    else proxy.put("token", user);
                    copy(uri, proxy, "sni", "sni");
                    copy(uri, proxy, "congestion-controller", "congestion_control");
                    copy(uri, proxy, "udp-relay-mode", "udp_relay_mode");
                    break;
                }
                case "wireguard": case "wg": {
                    proxy.put("type", "wireguard").put("private-key", uri.user());
                    copy(uri, proxy, "public-key", "publickey", "public_key", "public-key");
                    copy(uri, proxy, "pre-shared-key", "presharedkey", "pre_shared_key", "psk");
                    String address = uri.first("ip", "address", "local_address");
                    if (!address.isEmpty()) {
                        for (String item : address.split(",")) {
                            String ip = item.trim().split("/")[0];
                            if (ip.isEmpty()) continue;
                            if (ip.contains(":")) { if (!proxy.has("ipv6")) proxy.put("ipv6", ip); }
                            else if (!proxy.has("ip")) proxy.put("ip", ip);
                        }
                    }
                    putInt(proxy, "mtu", uri.first("mtu"));
                    String reserved = uri.first("reserved");
                    if (!reserved.isEmpty()) {
                        JSONArray values = new JSONArray();
                        for (String part : reserved.split(",")) values.put(Integer.parseInt(part.trim()));
                        proxy.put("reserved", values);
                    }
                    break;
                }
                case "anytls":
                    proxy.put("type", "anytls").put("password", uri.user());
                    copy(uri, proxy, "sni", "sni");
                    break;
                default:
                    throw new IllegalArgumentException("Неподдерживаемый протокол.");
            }
            String insecure = uri.first("insecure", "allow_insecure", "allowInsecure", "skip-cert-verify");
            if (!insecure.isEmpty()) proxy.put("skip-cert-verify", insecure.equals("1") || insecure.equalsIgnoreCase("true") || insecure.equalsIgnoreCase("yes"));
            String alpn = uri.first("alpn");
            if (!alpn.isEmpty()) proxy.put("alpn", new JSONArray(alpn.split(",")));
            if (!validProxy(proxy)) throw new IllegalArgumentException("У сервера не хватает имени, типа, адреса или порта.");
            return proxy;
        } catch (IllegalArgumentException error) { throw error; }
        catch (Exception error) { throw new IllegalArgumentException("Ссылка повреждена или содержит неподдерживаемые параметры."); }
    }

    private static void copy(Links.Parsed uri, JSONObject target, String targetName, String... sources) throws Exception {
        String value = uri.first(sources);
        if (!value.trim().isEmpty()) target.put(targetName, value);
    }

    private static void putInt(JSONObject target, String name, String value) throws Exception {
        try { if (!value.trim().isEmpty()) target.put(name, Integer.parseInt(value.trim().replaceAll("[^0-9]", ""))); }
        catch (NumberFormatException ignored) { }
    }

    private static JSONObject mapSsr(String value) throws Exception {
        String payload = Links.strictUtf8(Links.base64(value.trim().substring("ssr://".length())));
        int queryAt = payload.indexOf("/?");
        String main = queryAt >= 0 ? payload.substring(0, queryAt) : payload;
        Links.Parsed q = Links.parse("x://h:1?" + (queryAt >= 0 ? payload.substring(queryAt + 2) : ""));
        String[] parts = main.split(":");
        if (parts.length != 6) throw new IllegalArgumentException("SSR-ключ повреждён.");
        int port = Integer.parseInt(parts[1]);
        JSONObject proxy = new JSONObject().put("name", decodeOptional(q.q("remarks"), parts[0])).put("type", "ssr")
                .put("server", parts[0]).put("port", port).put("cipher", parts[3])
                .put("password", Links.strictUtf8(Links.base64(parts[5]))).put("protocol", parts[2]).put("obfs", parts[4]).put("udp", true);
        String protocolParam = decodeOptional(q.q("protoparam"), "");
        if (!protocolParam.isEmpty()) proxy.put("protocol-param", protocolParam);
        String obfsParam = decodeOptional(q.q("obfsparam"), "");
        if (!obfsParam.isEmpty()) proxy.put("obfs-param", obfsParam);
        return proxy;
    }

    private static String decodeOptional(String value, String fallback) {
        if (value == null || value.trim().isEmpty()) return fallback;
        try { return Links.cleanName(Links.strictUtf8(Links.base64(value))); } catch (Exception error) { return fallback; }
    }

    private static JSONObject mapShadowsocks(String value) throws Exception {
        String text = value.trim().substring("ss://".length());
        int hash = text.indexOf('#');
        String fragment = hash >= 0 ? Links.decode(text.substring(hash + 1)) : "";
        if (hash >= 0) text = text.substring(0, hash);
        int queryAt = text.indexOf('?');
        Links.Parsed query = Links.parse("x://h:1" + (queryAt >= 0 ? text.substring(queryAt) : ""));
        if (queryAt >= 0) text = text.substring(0, queryAt);
        if (text.endsWith("/")) text = text.substring(0, text.length() - 1);
        String credentials, address;
        int at = text.lastIndexOf('@');
        if (at >= 0) { credentials = Links.decode(text.substring(0, at)); address = text.substring(at + 1); }
        else {
            String decoded = Links.strictUtf8(Links.base64(text));
            at = decoded.lastIndexOf('@');
            if (at < 1) throw new IllegalArgumentException("Shadowsocks-ключ повреждён.");
            credentials = decoded.substring(0, at);
            address = decoded.substring(at + 1);
        }
        if (credentials.indexOf(':') < 0) credentials = Links.strictUtf8(Links.base64(credentials));
        int split = credentials.indexOf(':');
        if (split < 1) throw new IllegalArgumentException("Shadowsocks-ключ повреждён.");
        Links.Parsed server = Links.parse("ss://x@" + address);
        if (!Links.hasServer(server)) throw new IllegalArgumentException("В Shadowsocks-ключе нет сервера или порта.");
        String label = Links.cleanName(fragment);
        JSONObject proxy = new JSONObject().put("name", label.isEmpty() ? server.host : label).put("type", "ss")
                .put("server", server.host).put("port", server.port).put("cipher", credentials.substring(0, split))
                .put("password", credentials.substring(split + 1)).put("udp", true);
        String plugin = query.q("plugin");
        if (!plugin.trim().isEmpty()) {
            String[] parts = plugin.split(";");
            proxy.put("plugin", parts[0]);
            JSONObject options = new JSONObject();
            for (int i = 1; i < parts.length; i++) {
                int equals = parts[i].indexOf('=');
                if (equals > 0) options.put(parts[i].substring(0, equals).replace('_', '-'), parts[i].substring(equals + 1));
                else if (!parts[i].isEmpty()) options.put(parts[i].replace('_', '-'), true);
            }
            if (options.length() > 0) proxy.put("plugin-opts", options);
        }
        return proxy;
    }

    private static JSONObject mapSingBox(JSONObject source) {
        try {
            if (source == null || !source.has("type") || !source.has("server") || !source.has("server_port")) return null;
            String type = source.optString("type").toLowerCase(Locale.ROOT);
            Set<String> supported = new HashSet<>(java.util.Arrays.asList("shadowsocks", "socks", "http", "vmess", "vless", "trojan",
                    "hysteria", "hysteria2", "tuic", "wireguard", "anytls", "ssh"));
            if (!supported.contains(type)) return null;
            int port = Integer.parseInt(source.optString("server_port").trim());
            String tag = source.optString("tag", source.optString("server"));
            JSONObject proxy = new JSONObject().put("name", Links.cleanName(tag).isEmpty() ? source.optString("server") : Links.cleanName(tag))
                    .put("type", type.equals("shadowsocks") ? "ss" : type.equals("socks") ? "socks5" : type)
                    .put("server", source.optString("server")).put("port", port).put("udp", true);
            String[][] keys = {{"uuid", "uuid"}, {"password", "password"}, {"method", "cipher"}, {"alter_id", "alterId"},
                    {"security", "cipher"}, {"flow", "flow"}, {"network", "network"}, {"username", "username"}, {"private_key", "private-key"},
                    {"public_key", "public-key"}, {"peer_public_key", "public-key"}, {"pre_shared_key", "pre-shared-key"}, {"token", "token"},
                    {"up_mbps", "up"}, {"down_mbps", "down"}, {"auth", "auth-str"}, {"auth_str", "auth-str"},
                    {"protocol", "protocol"}, {"congestion_control", "congestion-controller"}, {"udp_relay_mode", "udp-relay-mode"},
                    {"recv_window_conn", "recv-window-conn"}, {"recv_window", "recv-window"}, {"disable_mtu_discovery", "disable-mtu-discovery"},
                    {"mtu", "mtu"}, {"heartbeat", "heartbeat-interval"}, {"obfs_password", "obfs-password"}};
            for (String[] pair : keys) if (source.has(pair[0]) && !source.isNull(pair[0])) proxy.put(pair[1], source.get(pair[0]));
            JSONObject tls = source.optJSONObject("tls");
            if (tls != null) {
                boolean native_ = type.equals("vmess") || type.equals("vless") || type.equals("trojan");
                if (native_ && tls.has("enabled")) proxy.put("tls", tls.get("enabled"));
                if (tls.has("server_name")) proxy.put(native_ ? "servername" : "sni", tls.get("server_name"));
                if (tls.has("insecure")) proxy.put("skip-cert-verify", tls.get("insecure"));
                if (tls.has("alpn")) proxy.put("alpn", tls.get("alpn"));
                JSONObject reality = tls.optJSONObject("reality");
                if (reality != null) {
                    JSONObject options = new JSONObject();
                    if (reality.has("public_key")) options.put("public-key", reality.get("public_key"));
                    if (reality.has("short_id")) options.put("short-id", reality.get("short_id"));
                    if (options.length() > 0) proxy.put("reality-opts", options);
                }
                JSONObject utls = tls.optJSONObject("utls");
                if (utls != null && utls.has("fingerprint")) proxy.put("client-fingerprint", utls.get("fingerprint"));
            }
            JSONObject obfs = source.optJSONObject("obfs");
            if (obfs != null) {
                if (obfs.has("type")) proxy.put("obfs", obfs.get("type"));
                if (obfs.has("password")) proxy.put("obfs-password", obfs.get("password"));
            } else if (source.opt("obfs") instanceof String) proxy.put("obfs", source.get("obfs"));
            if (type.equals("wireguard") && source.has("local_address")) {
                JSONArray addresses = source.optJSONArray("local_address");
                if (addresses == null) addresses = new JSONArray().put(source.opt("local_address"));
                for (int i = 0; i < addresses.length(); i++) {
                    String ip = String.valueOf(addresses.opt(i)).split("/")[0];
                    if (ip.contains(":")) { if (!proxy.has("ipv6")) proxy.put("ipv6", ip); }
                    else if (!proxy.has("ip")) proxy.put("ip", ip);
                }
            }
            if (type.equals("wireguard") && source.has("reserved")) proxy.put("reserved", source.get("reserved"));
            JSONObject transport = source.optJSONObject("transport");
            if (transport != null && transport.has("type")) {
                String network = transport.optString("type");
                proxy.put("network", network);
                JSONObject options = new JSONObject();
                if (transport.has("path")) options.put("path", transport.get("path"));
                if (transport.has("headers")) options.put("headers", transport.get("headers"));
                if (transport.has("service_name")) options.put("grpc-service-name", transport.get("service_name"));
                if (transport.has("max_early_data")) options.put("max-early-data", transport.get("max_early_data"));
                if (transport.has("early_data_header_name")) options.put("early-data-header-name", transport.get("early_data_header_name"));
                if (options.length() > 0) proxy.put(network + "-opts", options);
            }
            return validProxy(proxy) ? proxy : null;
        } catch (Exception error) { return null; }
    }

    private static JSONObject mapSip008(JSONObject source) {
        try {
            if (source == null || !source.has("server") || !source.has("server_port") || !source.has("method") || !source.has("password")) return null;
            int port = Integer.parseInt(source.optString("server_port").trim());
            if (port < 1 || port > 65535) return null;
            String label = source.optString("remarks", source.optString("name", source.optString("server")));
            JSONObject proxy = new JSONObject().put("name", Links.cleanName(label).isEmpty() ? source.optString("server") : Links.cleanName(label))
                    .put("type", "ss").put("server", source.optString("server")).put("port", port)
                    .put("cipher", source.optString("method")).put("password", source.optString("password")).put("udp", true);
            if (!source.optString("plugin").trim().isEmpty()) proxy.put("plugin", source.optString("plugin"));
            Object options = source.opt("plugin_opts");
            if (options instanceof String) {
                JSONObject map = new JSONObject();
                for (String item : ((String) options).split(";")) {
                    int equals = item.indexOf('=');
                    if (equals > 0) map.put(item.substring(0, equals).replace('_', '-'), item.substring(equals + 1));
                }
                if (map.length() > 0) proxy.put("plugin-opts", map);
            } else if (options != null && options != JSONObject.NULL) proxy.put("plugin-opts", options);
            return proxy;
        } catch (Exception error) { return null; }
    }

    private static boolean hasPlugin(JSONObject source) {
        return source != null && (!source.optString("plugin").trim().isEmpty() || source.has("plugin_opts"));
    }

    private static boolean validProxy(JSONObject proxy) {
        if (proxy == null) return false;
        try {
            if (proxy.optString("name").trim().isEmpty() || proxy.optString("type").trim().isEmpty() || proxy.optString("server").trim().isEmpty()) return false;
            int port = Integer.parseInt(proxy.optString("port").trim());
            if (port < 1 || port > 65535) return false;
            if ("wireguard".equalsIgnoreCase(proxy.optString("type"))) {
                JSONArray addresses = proxy.optJSONArray("ip");
                if (addresses != null) {
                    proxy.remove("ip");
                    for (int i = 0; i < addresses.length(); i++) {
                        String ip = addresses.optString(i);
                        if (ip.contains(":")) { if (!proxy.has("ipv6")) proxy.put("ipv6", ip); }
                        else if (!proxy.has("ip")) proxy.put("ip", ip);
                    }
                }
            }
            return true;
        } catch (Exception error) { return false; }
    }
}
