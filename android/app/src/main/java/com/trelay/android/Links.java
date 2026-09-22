package com.trelay.android;

import java.net.IDN;
import java.net.URLDecoder;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import org.json.JSONObject;

// Port of Windows ConnectionValidator: tolerant share-link parsing, validation and names.
final class Links {
    static final int MAX_KEY_LENGTH = 64 * 1024;

    private Links() { }

    static final class Parsed {
        String scheme = "";
        String rawUser = "";
        String host = "";
        int port = -1;
        String path = "";
        final Map<String, String> query = new LinkedHashMap<>();
        String fragment = "";

        String user() { return decode(rawUser); }
        String q(String key) { String v = query.get(key); return v == null ? "" : v; }
        String q(String key, String fallback) { String v = query.get(key); return v == null || v.trim().isEmpty() ? fallback : v; }
        String first(String... keys) { for (String key : keys) { String v = query.get(key); if (v != null && !v.trim().isEmpty()) return v; } return ""; }
    }

    static String scheme(String value) {
        if (value == null) return "";
        int split = value.indexOf("://");
        return split <= 0 ? "" : value.substring(0, split).trim().toLowerCase(Locale.ROOT);
    }

    static Parsed parse(String value) {
        if (value == null) throw new IllegalArgumentException("Пустая ссылка.");
        value = value.trim();
        Parsed result = new Parsed();
        int split = value.indexOf("://");
        if (split <= 0) throw new IllegalArgumentException("Ссылка повреждена или обрезана.");
        result.scheme = value.substring(0, split).toLowerCase(Locale.ROOT);
        String rest = value.substring(split + 3);
        int hash = rest.indexOf('#');
        if (hash >= 0) { result.fragment = decode(rest.substring(hash + 1)); rest = rest.substring(0, hash); }
        int question = rest.indexOf('?');
        if (question >= 0) {
            for (String pair : rest.substring(question + 1).split("&")) {
                if (pair.isEmpty()) continue;
                int equals = pair.indexOf('=');
                String key = decode(equals < 0 ? pair : pair.substring(0, equals));
                String item = equals < 0 ? "" : decode(pair.substring(equals + 1));
                if (!result.query.containsKey(key)) result.query.put(key, item);
            }
            rest = rest.substring(0, question);
        }
        int slash = rest.indexOf('/');
        if (slash >= 0) { result.path = rest.substring(slash); rest = rest.substring(0, slash); }
        int at = rest.lastIndexOf('@');
        if (at >= 0) { result.rawUser = rest.substring(0, at); rest = rest.substring(at + 1); }
        if (rest.startsWith("[")) {
            int close = rest.indexOf(']');
            if (close < 0) throw new IllegalArgumentException("Некорректный IPv6-адрес сервера.");
            result.host = rest.substring(1, close);
            rest = rest.substring(close + 1);
            if (rest.startsWith(":")) result.port = parsePort(rest.substring(1));
        } else {
            int colon = rest.lastIndexOf(':');
            if (colon >= 0) { result.host = rest.substring(0, colon); result.port = parsePort(rest.substring(colon + 1)); }
            else result.host = rest;
        }
        result.host = decode(result.host);
        return result;
    }

    private static int parsePort(String text) {
        try { int port = Integer.parseInt(text.trim()); return port >= 1 && port <= 65535 ? port : -1; }
        catch (NumberFormatException error) { return -1; }
    }

    static String decode(String value) {
        if (value == null) return "";
        try { return URLDecoder.decode(value.replace("+", "%2B"), "UTF-8"); }
        catch (Exception error) { return value; }
    }

    static byte[] base64(String value) {
        String text = value == null ? "" : value.trim().replaceAll("\\s", "").replace('-', '+').replace('_', '/');
        while (text.length() % 4 != 0) text += "=";
        return Base64.getDecoder().decode(text);
    }

    static String base64Url(byte[] value) {
        return Base64.getUrlEncoder().withoutPadding().encodeToString(value);
    }

    static String strictUtf8(byte[] bytes) throws CharacterCodingException {
        return StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString();
    }

    static boolean hasServer(Parsed link) {
        return link.host != null && !link.host.trim().isEmpty() && link.port >= 1 && link.port <= 65535;
    }

    static String cleanName(String value) {
        if (value == null) return "";
        StringBuilder clean = new StringBuilder();
        value.codePoints().filter(c -> !Character.isISOControl(c)).forEach(clean::appendCodePoint);
        String result = clean.toString().trim();
        return result.length() <= 80 ? result : result.substring(0, 80).trim();
    }

    static boolean isSupportedLink(String value) {
        value = value == null ? "" : value.trim();
        String scheme = scheme(value);
        return scheme.equals("vless") || scheme.equals("vmess") || scheme.equals("trojan") || scheme.equals("ss") ||
                scheme.equals("socks") || scheme.equals("socks5") || value.startsWith(XrayConfigs.PREFIX) ||
                value.startsWith(MihomoConfig.PREFIX) || MihomoConfig.isDirectLink(value) ||
                scheme.equals("key") || scheme.equals("ssconf");
    }

    static boolean isDynamic(String value) {
        String scheme = scheme(value);
        return scheme.equals("key") || scheme.equals("ssconf") || scheme.equals("https");
    }

    /** Engine that runs the link: "xray", "mihomo" or "dynamic" (resolved at connect time). */
    static String engine(String value) {
        value = value == null ? "" : value.trim();
        if (value.startsWith(MihomoConfig.PREFIX) || MihomoConfig.isDirectLink(value)) return "mihomo";
        if (isDynamic(value)) return "dynamic";
        return "xray";
    }

    static String kindLabel(String value) {
        value = value == null ? "" : value.trim();
        if (value.startsWith(XrayConfigs.PREFIX)) return "XRAY";
        if (value.startsWith(MihomoConfig.PREFIX)) return MihomoConfig.typeLabel(value);
        String scheme = scheme(value);
        switch (scheme) {
            case "hy2": case "hysteria2": return "HYSTERIA2";
            case "wg": return "WIREGUARD";
            case "socks": return "SOCKS5";
            case "ss": return MihomoConfig.isDirectLink(value) ? "SS+PLUGIN" : "SS";
            default: return scheme.toUpperCase(Locale.ROOT);
        }
    }

    static String suggestName(String value) {
        value = value == null ? "" : value.trim();
        if (value.isEmpty() || value.length() > MAX_KEY_LENGTH) return "";
        try {
            if (value.startsWith(XrayConfigs.PREFIX)) return XrayConfigs.embeddedName(value);
            if (value.startsWith(MihomoConfig.PREFIX)) return MihomoConfig.name(value);
            if (MihomoConfig.isDirectLink(value)) return MihomoConfig.name(MihomoConfig.encodeDirect(value));
            if (scheme(value).equals("vmess")) {
                JSONObject root = new JSONObject(strictUtf8(base64(value.substring("vmess://".length()))));
                return cleanName(root.optString("ps", root.optString("add")));
            }
            if (isDynamic(value)) return "";
            Parsed link = parse(value);
            String label = cleanName(link.fragment);
            return label.isEmpty() ? cleanName(link.host) : label;
        } catch (Exception error) { return ""; }
    }

    static String endpoint(String value) {
        try {
            if (value.startsWith(XrayConfigs.PREFIX)) return XrayConfigs.embeddedEndpoint(value);
            if (value.startsWith(MihomoConfig.PREFIX)) return MihomoConfig.endpoint(value);
            if (scheme(value).equals("vmess")) {
                JSONObject root = new JSONObject(strictUtf8(base64(value.substring("vmess://".length()))));
                return root.optString("add") + ":" + root.optString("port");
            }
            if (isDynamic(value)) return scheme(value).toUpperCase(Locale.ROOT);
            Parsed link = parse(value);
            return (link.host.contains(":") ? "[" + link.host + "]" : link.host) + ":" + link.port;
        } catch (Exception error) { return "сервер"; }
    }

    /** Throws IllegalArgumentException with a user-facing message when the key is unusable. */
    static String validate(String value) {
        value = value == null ? "" : value.trim();
        if (value.isEmpty()) throw new IllegalArgumentException("Вставь ключ или ссылку подключения.");
        if (value.length() > MAX_KEY_LENGTH) throw new IllegalArgumentException("Ключ превышает допустимый размер (64 КБ).");
        for (int i = 0; i < value.length(); i++)
            if (Character.isISOControl(value.charAt(i))) throw new IllegalArgumentException("Ключ содержит недопустимые управляющие символы.");
        String scheme = scheme(value);
        if (scheme.equals("vmess")) return validateVmess(value);
        if (scheme.equals("key")) { ShareLinkResolver.decodeKeyContainer(value); return "KEY-ссылка проверена. Конфигурация обновится при подключении."; }
        if (scheme.equals("ssconf")) { ShareLinkResolver.ssconfUri(value); return "SSCONF-ссылка проверена. Конфигурация обновится при подключении."; }
        if (scheme.equals("https")) { ShareLinkResolver.subscriptionUri(value); return "HTTPS-подписка проверена."; }
        if (value.startsWith(XrayConfigs.PREFIX)) { XrayConfigs.decodeEmbedded(value); return "Xray-конфиг проверен."; }
        if (value.startsWith(MihomoConfig.PREFIX)) { MihomoConfig.decode(value); return "Конфигурация Mihomo проверена."; }
        if (MihomoConfig.isDirectLink(value)) { MihomoConfig.encodeDirect(value); return kindLabel(value) + "-ключ проверен."; }
        Parsed link = parse(value);
        switch (scheme) {
            case "vless":
                if (!validVlessId(link.user())) throw new IllegalArgumentException("В VLESS-ключе некорректный идентификатор клиента.");
                if (!hasServer(link)) throw new IllegalArgumentException("В VLESS-ключе нет сервера или порта.");
                validateTransport(link.q("type"), link.q("security"), link.q("sni"), link.q("pbk"), link.q("sid"));
                return "VLESS-ключ проверен.";
            case "trojan":
                if (link.user().trim().isEmpty()) throw new IllegalArgumentException("В Trojan-ключе нет пароля.");
                if (!hasServer(link)) throw new IllegalArgumentException("В Trojan-ключе нет сервера или порта.");
                validateTransport(link.q("type"), link.q("security"), link.q("sni"), link.q("pbk"), link.q("sid"));
                return "Trojan-ключ проверен.";
            case "ss": {
                if (!hasServer(link)) throw new IllegalArgumentException("В Shadowsocks-ключе нет сервера или порта.");
                String credentials = shadowsocksCredentials(link.rawUser);
                int colon = credentials.indexOf(':');
                if (colon < 1 || colon == credentials.length() - 1) throw new IllegalArgumentException("В Shadowsocks-ключе нет метода или пароля.");
                return "Shadowsocks TCP/UDP ключ проверен.";
            }
            case "socks": case "socks5": {
                if (!hasServer(link)) throw new IllegalArgumentException("В SOCKS5-ссылке нет сервера или порта.");
                String credentials = link.user();
                if (!credentials.isEmpty()) {
                    if (credentials.indexOf(':') < 0) credentials = decodeMaybeBase64(credentials);
                    if (credentials.indexOf(':') < 1 || credentials.endsWith(":"))
                        throw new IllegalArgumentException("В SOCKS5-ссылке некорректные логин или пароль.");
                }
                return "SOCKS5-ссылка проверена.";
            }
            default:
                throw new IllegalArgumentException("Поддерживаются VLESS, VMess, Trojan, Shadowsocks, SOCKS5, Hysteria 2, TUIC, SSR, WireGuard, AnyTLS, KEY, SSCONF, HTTPS-подписки, Clash, sing-box и Xray.");
        }
    }

    private static String validateVmess(String value) {
        JSONObject root;
        try { root = new JSONObject(strictUtf8(base64(value.substring("vmess://".length())))); }
        catch (Exception error) { throw new IllegalArgumentException("VMess-ключ повреждён или обрезан."); }
        try { UUID.fromString(root.optString("id")); }
        catch (Exception error) { throw new IllegalArgumentException("В VMess-ключе некорректный UUID."); }
        int port;
        try { port = Integer.parseInt(root.optString("port").trim()); } catch (Exception error) { port = -1; }
        if (root.optString("add").trim().isEmpty() || port < 1 || port > 65535)
            throw new IllegalArgumentException("В VMess-ключе некорректный сервер или порт.");
        validateTransport(root.optString("net"), root.optString("tls"), root.optString("sni"), "", "");
        return "VMess-ключ проверен.";
    }

    static void validateTransport(String network, String security, String sni, String publicKey, String shortId) {
        network = empty(network, "tcp");
        security = empty(security, "none");
        if (!(network.equals("tcp") || network.equals("raw") || network.equals("ws") || network.equals("grpc") ||
                network.equals("xhttp") || network.equals("splithttp") || network.equals("httpupgrade")))
            throw new IllegalArgumentException("Этот транспорт ссылки пока не поддерживается. Импортируй полный Xray-конфиг.");
        if (!(security.equals("none") || security.equals("tls") || security.equals("reality")))
            throw new IllegalArgumentException("В ключе указан неподдерживаемый тип защиты соединения.");
        if (security.equals("reality")) {
            if (sni.trim().isEmpty() || publicKey.trim().isEmpty())
                throw new IllegalArgumentException("В REALITY-ключе не хватает SNI или открытого ключа сервера.");
            if (shortId.length() > 16 || shortId.length() % 2 != 0 || !shortId.matches("[0-9a-fA-F]*"))
                throw new IllegalArgumentException("В REALITY-ключе некорректный shortId.");
        }
    }

    static boolean validVlessId(String value) {
        return value != null && value.length() >= 1 && value.length() <= 128 && value.matches("[A-Za-z0-9_.~-]+");
    }

    static String shadowsocksCredentials(String rawUser) {
        String value = decode(rawUser);
        if (value.indexOf(':') >= 0) return value;
        return decodeMaybeBase64(value);
    }

    private static String decodeMaybeBase64(String value) {
        try { return strictUtf8(base64(value)); }
        catch (Exception error) { return value; }
    }

    static String empty(String value, String fallback) {
        return value == null || value.trim().isEmpty() ? fallback : value.trim();
    }

    static String asciiHost(String host) {
        try { return IDN.toASCII(host); } catch (Exception error) { return host; }
    }
}
