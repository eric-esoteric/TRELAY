package com.trelay.android;

import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.net.ConnectException;
import java.net.HttpURLConnection;
import java.net.InetSocketAddress;
import java.net.Proxy;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import java.net.URI;
import java.net.URL;
import java.net.URLEncoder;
import java.net.UnknownHostException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import javax.net.ssl.SSLException;
import org.json.JSONArray;
import org.json.JSONObject;
import org.json.JSONTokener;

// Port of Windows ShareLinkResolver: key://, ssconf://, HTTPS subscriptions (Happ/Remnawave X-HWID).
final class ShareLinkResolver {
    static class ResolutionException extends Exception {
        ResolutionException(String message) { super(message); }
        ResolutionException(String message, Throwable cause) { super(message, cause); }
    }

    static final class IdentityRequiredException extends ResolutionException {
        final String host;
        IdentityRequiredException(String host) { super("Провайдер подписки требует постоянный идентификатор устройства."); this.host = host; }
    }

    static final class DeviceLimitException extends ResolutionException {
        DeviceLimitException(String message) { super(message); }
    }

    /** Platform hooks (Android implementation lives in TrelayApp). */
    interface Platform {
        String systemTxt(String host) throws Exception;
        AutoCloseable startFragmentProxy(int[] portOut) throws Exception;
        String userAgent();
        String deviceOs();
        String deviceModel();
        String osVersion();
    }

    static volatile Platform platform;

    private static final int MAX_DOWNLOAD = 2 * 1024 * 1024;
    private static final String KEY_DNS_SUFFIX = "config-delivery.dev";
    private static final String KEY_BOOTSTRAP_SECRET = BuildConfig.TRELAY_BOOTSTRAP_SECRET;

    private ShareLinkResolver() { }

    static String resolve(String value) throws ResolutionException { return resolveMany(value, null).get(0); }

    static List<String> resolveMany(String value, String clientId) throws ResolutionException {
        LinkedHashSet<String> result = new LinkedHashSet<>();
        resolveInto(value == null ? "" : value.trim(), clientId, 0, result);
        if (result.isEmpty()) throw new ResolutionException("Ссылка не содержит поддерживаемых VPN-серверов.");
        List<String> list = new ArrayList<>(result);
        return list.size() > 128 ? new ArrayList<>(list.subList(0, 128)) : list;
    }

    private static void resolveInto(String value, String clientId, int depth, LinkedHashSet<String> result) throws ResolutionException {
        if (depth >= 4) throw new ResolutionException("Ссылка содержит слишком длинную цепочку вложенных конфигураций.");
        String scheme = Links.scheme(value);
        if (scheme.equals("key")) { resolveInto(resolveKey(value), clientId, depth + 1, result); return; }
        if (scheme.equals("ssconf")) { for (String link : resolveSsconf(value)) resolveInto(link, clientId, depth + 1, result); return; }
        if (scheme.equals("https")) { for (String link : resolveSubscription(value, clientId)) resolveInto(link, clientId, depth + 1, result); return; }
        if (MihomoConfig.isDirectLink(value)) {
            try { result.add(MihomoConfig.encodeDirect(value)); } catch (IllegalArgumentException error) { if (depth == 0) throw new ResolutionException(error.getMessage()); }
            return;
        }
        if (value.indexOf('\n') < 0 && Links.isSupportedLink(value)) { result.add(value.trim()); return; }
        // Pasted file content (Clash YAML, sing-box/Xray JSON, base64 list).
        if (depth == 0 && value.length() > 16) {
            for (String link : extractSupportedLinks(value, "Текст не содержит поддерживаемых VPN-серверов."))
                resolveInto(link, clientId, depth + 1, result);
        }
    }

    // ------------------------------------------------------------------ key://

    static final class KeyContainer { final String orderId; final byte[] secret; KeyContainer(String o, byte[] s) { orderId = o; secret = s; } }

    static KeyContainer decodeKeyContainer(String value) {
        try {
            value = value.trim();
            if (!value.toLowerCase(Locale.ROOT).startsWith("key://")) throw new IllegalArgumentException();
            byte[] bootstrap = java.util.Base64.getDecoder().decode(KEY_BOOTSTRAP_SECRET);
            JSONObject root = new JSONObject(decryptEnvelope(value.substring(6), bootstrap));
            String orderId = root.getString("orderId").trim();
            if (orderId.isEmpty() || orderId.length() > 63 || !orderId.matches("[\\p{L}\\p{Nd}-]+")) throw new IllegalArgumentException();
            byte[] secret = java.util.Base64.getDecoder().decode(root.getString("key"));
            if (secret.length != 32) throw new IllegalArgumentException();
            return new KeyContainer(orderId, secret);
        } catch (Exception error) {
            throw new IllegalArgumentException("KEY-ссылка повреждена или не прошла проверку целостности.");
        }
    }

    private static String resolveKey(String value) throws ResolutionException {
        KeyContainer container;
        try { container = decodeKeyContainer(value); }
        catch (IllegalArgumentException error) { throw new ResolutionException(error.getMessage()); }
        try {
            String host = container.orderId + "." + KEY_DNS_SUFFIX;
            String payload = null;
            Exception dohError = null;
            try { payload = queryTxtOverHttps(host); } catch (Exception error) { dohError = error; }
            if (payload == null || payload.trim().isEmpty()) {
                try { payload = platform == null ? null : platform.systemTxt(host); }
                catch (Exception error) { throw new ResolutionException("Для KEY-ссылки не найден актуальный VPN-конфиг. Проверь интернет или срок действия ключа.", dohError != null ? dohError : error); }
            }
            if (payload == null || payload.trim().isEmpty())
                throw new ResolutionException("Для KEY-ссылки не найден актуальный VPN-конфиг. Проверь интернет или срок действия ключа.");
            String plain = decryptEnvelope(payload, container.secret);
            return extractSupportedLinks(plain, "KEY-ссылка вернула неподдерживаемую конфигурацию.").get(0);
        } catch (ResolutionException error) { throw error; }
        catch (Exception error) { throw new ResolutionException("Не удалось расшифровать актуальную конфигурацию KEY-ссылки.", error); }
        finally { Arrays.fill(container.secret, (byte) 0); }
    }

    static String decryptEnvelope(String encoded, byte[] key) throws Exception {
        if (key == null || key.length != 32) throw new SecurityException();
        JSONObject envelope = new JSONObject(Links.strictUtf8(Links.base64(encoded)));
        String ivText = envelope.getString("iv");
        String valueText = envelope.getString("value");
        String macText = envelope.optString("mac");
        if (macText.trim().isEmpty()) throw new SecurityException();
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(key, "HmacSHA256"));
        byte[] expected = mac.doFinal((ivText + valueText).getBytes(StandardCharsets.UTF_8));
        if (!MessageDigest.isEqual(expected, hex(macText))) throw new SecurityException();
        byte[] iv = java.util.Base64.getDecoder().decode(ivText);
        byte[] encrypted = java.util.Base64.getDecoder().decode(valueText);
        if (iv.length != 16 || encrypted.length == 0 || encrypted.length % 16 != 0) throw new SecurityException();
        Cipher cipher = Cipher.getInstance("AES/CBC/PKCS5Padding");
        cipher.init(Cipher.DECRYPT_MODE, new SecretKeySpec(key, "AES"), new IvParameterSpec(iv));
        byte[] plain = cipher.doFinal(encrypted);
        try { return Links.strictUtf8(plain); } finally { Arrays.fill(plain, (byte) 0); }
    }

    private static byte[] hex(String value) {
        value = value.trim();
        if (value.isEmpty() || value.length() % 2 != 0) throw new SecurityException();
        byte[] bytes = new byte[value.length() / 2];
        for (int i = 0; i < bytes.length; i++) bytes[i] = (byte) Integer.parseInt(value.substring(i * 2, i * 2 + 2), 16);
        return bytes;
    }

    private static String queryTxtOverHttps(String host) throws Exception {
        URL url = new URL("https://cloudflare-dns.com/dns-query?name=" + URLEncoder.encode(host, "UTF-8") + "&type=TXT");
        HttpURLConnection connection = (HttpURLConnection) url.openConnection();
        try {
            connection.setInstanceFollowRedirects(false);
            connection.setConnectTimeout(8000);
            connection.setReadTimeout(8000);
            connection.setRequestProperty("Accept", "application/dns-json");
            connection.setRequestProperty("User-Agent", userAgent());
            if (connection.getResponseCode() != 200) throw new IOException("DNS over HTTPS failed.");
            JSONObject root;
            try (InputStream input = connection.getInputStream()) { root = new JSONObject(Links.strictUtf8(readLimited(input))); }
            if (root.optInt("Status", -1) != 0) throw new IOException("DNS response status is not successful.");
            JSONArray answers = root.optJSONArray("Answer");
            if (answers == null) return null;
            for (int i = 0; i < answers.length(); i++) {
                JSONObject answer = answers.optJSONObject(i);
                if (answer != null && answer.optInt("type") == 16) {
                    String decoded = decodeDnsJsonTxt(answer.optString("data"));
                    if (!decoded.trim().isEmpty()) return decoded;
                }
            }
            return null;
        } finally { connection.disconnect(); }
    }

    static String decodeDnsJsonTxt(String value) {
        value = value == null ? "" : value.trim();
        if (value.isEmpty()) return "";
        StringBuilder result = new StringBuilder();
        int index = 0;
        while (index < value.length()) {
            while (index < value.length() && Character.isWhitespace(value.charAt(index))) index++;
            if (index >= value.length()) break;
            if (value.charAt(index) != '"') return value;
            index++;
            while (index < value.length() && value.charAt(index) != '"') {
                if (value.charAt(index) == '\\' && index + 1 < value.length()) {
                    index++;
                    char c = value.charAt(index);
                    if (c >= '0' && c <= '7') {
                        int octal = 0, count = 0;
                        while (index < value.length() && count < 3 && value.charAt(index) >= '0' && value.charAt(index) <= '7') {
                            octal = octal * 8 + value.charAt(index++) - '0';
                            count++;
                        }
                        result.append((char) octal);
                        continue;
                    }
                }
                result.append(value.charAt(index++));
            }
            if (index >= value.length()) throw new IllegalArgumentException();
            index++;
        }
        return result.toString();
    }

    // ------------------------------------------------------------------ ssconf:// and https://

    static URI ssconfUri(String value) {
        value = value == null ? "" : value.trim();
        if (!Links.scheme(value).equals("ssconf")) throw new IllegalArgumentException("SSCONF-ссылка повреждена или содержит небезопасный адрес.");
        return safeHttps("https://" + value.substring(value.indexOf("://") + 3), "SSCONF-ссылка повреждена или содержит небезопасный адрес.");
    }

    static URI subscriptionUri(String value) {
        return safeHttps(value, "Ссылка подписки повреждена или содержит небезопасный адрес.");
    }

    private static URI safeHttps(String value, String message) {
        try {
            value = value == null ? "" : value.trim();
            if (value.length() > 4096) throw new IllegalArgumentException();
            URI uri = new URI(value);
            String host = uri.getHost();
            if (!"https".equalsIgnoreCase(uri.getScheme()) || host == null || host.isEmpty() || uri.getUserInfo() != null ||
                    uri.getFragment() != null || host.equalsIgnoreCase("localhost") || host.startsWith("127.") || host.equals("[::1]"))
                throw new IllegalArgumentException();
            return uri;
        } catch (Exception error) { throw new IllegalArgumentException(message); }
    }

    private static List<String> resolveSsconf(String value) throws ResolutionException {
        URI uri;
        try { uri = ssconfUri(value); } catch (IllegalArgumentException error) { throw new ResolutionException(error.getMessage()); }
        try { return extractSupportedLinks(Links.strictUtf8(download(uri, null, false)), "SSCONF-ссылка вернула неподдерживаемую конфигурацию."); }
        catch (ResolutionException error) { throw error; }
        catch (HttpStatusException error) { throw new ResolutionException("Не удалось обновить SSCONF. Проверь интернет и доступность сервера конфигурации. Код сервера: " + error.status + ".", error); }
        catch (Exception error) { throw new ResolutionException("Не удалось обновить SSCONF. Проверь интернет и доступность сервера конфигурации.", error); }
    }

    private static List<String> resolveSubscription(String value, String clientId) throws ResolutionException {
        URI uri;
        try { uri = subscriptionUri(value); } catch (IllegalArgumentException error) { throw new ResolutionException(error.getMessage()); }
        boolean authenticated = clientId != null && !clientId.trim().isEmpty();
        try {
            List<String> links = extractSupportedLinks(Links.strictUtf8(download(uri, clientId, true)), "Подписка не содержит поддерживаемых VPN-серверов.");
            if (links.size() == 1 && isUnsupportedClientResponse(links.get(0))) {
                if (!authenticated) throw new IdentityRequiredException(uri.getHost());
                throw new ResolutionException("Сервис вернул заглушку для неизвестного клиента и не принял сохранённый ID TRELAY. По этому ответу нельзя определить причину; это не обязательно лимит устройств.");
            }
            List<String> clean = new ArrayList<>();
            for (String link : links) if (!isUnsupportedClientResponse(link)) clean.add(link);
            return clean;
        } catch (ResolutionException error) { throw error; }
        catch (HttpStatusException error) {
            int code = error.status;
            if (!authenticated && (code == 401 || code == 403)) throw new IdentityRequiredException(uri.getHost());
            ResolutionException headerError = headerError(error.connectionHeaders, authenticated, uri.getHost(), true);
            if (headerError != null) throw headerError;
            String suffix = " Код сервера: " + code + ".";
            if (code == 429) throw new ResolutionException("Сервис временно ограничил частоту запросов к подписке." + suffix, error);
            if (authenticated && (code == 401 || code == 403))
                throw new ResolutionException("Сервис отклонил сохранённый ID TRELAY." + suffix + " Ответ не показывает, связан ли отказ со сроком подписки, привязкой или правилом сервиса.", error);
            if (code >= 500) throw new ResolutionException("Сервис подписки временно недоступен." + suffix, error);
            throw new ResolutionException("Сервис подписки отклонил запрос." + suffix, error);
        } catch (UnknownHostException error) {
            throw new ResolutionException("DNS не смог найти сервис подписки. Проверь подключение и DNS.", error);
        } catch (SocketTimeoutException error) {
            throw new ResolutionException("Сервис подписки не ответил за 12 секунд после повторной попытки.", error);
        } catch (SSLException error) {
            throw new ResolutionException("Телефон не принял сертификат сервиса подписки. Проверь системное время.", error);
        } catch (IOException error) {
            throw new ResolutionException("Сервис подписки оборвал соединение. TRELAY повторил запрос с совместимыми настройками, но сервер снова закрыл его.", error);
        } catch (Exception error) {
            throw new ResolutionException("Не удалось прочитать HTTPS-подписку.", error);
        }
    }

    private static final class HttpStatusException extends IOException {
        final int status;
        final java.util.Map<String, List<String>> connectionHeaders;
        HttpStatusException(int status, java.util.Map<String, List<String>> headers) { super("HTTP " + status); this.status = status; connectionHeaders = headers; }
    }

    private static boolean transient_(Exception error) {
        return error instanceof SocketTimeoutException || error instanceof ConnectException || error instanceof SSLException ||
                error instanceof SocketException || error instanceof EOFException ||
                (error instanceof IOException && !(error instanceof UnknownHostException) && !(error instanceof HttpStatusException));
    }

    private static byte[] download(URI initial, String clientId, boolean inspectHeaders) throws Exception {
        int attempts = clientId == null || clientId.trim().isEmpty() ? 3 : 4;
        Exception last = null;
        for (int attempt = 0; attempt < attempts; attempt++) {
            try { return downloadOnce(initial, clientId, inspectHeaders, attempt, null); }
            catch (ResolutionException error) { throw error; }
            catch (Exception error) {
                last = error;
                if (!transient_(error)) throw error;
                // A reset or TLS failure without any HTTP response: try the fragmented transport at once.
                break;
            }
        }
        Platform hooks = platform;
        if (hooks == null) throw last;
        Exception fragmented = null;
        for (int attempt = 0; attempt < attempts; attempt++) {
            int[] port = new int[1];
            try (AutoCloseable ignored = hooks.startFragmentProxy(port)) {
                return downloadOnce(initial, clientId, inspectHeaders, attempt, new Proxy(Proxy.Type.HTTP, new InetSocketAddress("127.0.0.1", port[0])));
            } catch (ResolutionException error) { throw error; }
            catch (Exception error) {
                fragmented = error;
                if (!transient_(error) || attempt + 1 >= attempts) break;
                Thread.sleep(attempt == 0 ? 300 : attempt == 1 ? 800 : 1500);
            }
        }
        throw fragmented != null ? fragmented : last;
    }

    private static byte[] downloadOnce(URI initial, String clientId, boolean inspectHeaders, int attempt, Proxy proxy) throws Exception {
        URI uri = initial;
        for (int redirect = 0; redirect <= 3; redirect++) {
            HttpURLConnection connection = (HttpURLConnection) (proxy == null ? uri.toURL().openConnection() : uri.toURL().openConnection(proxy));
            try {
                connection.setInstanceFollowRedirects(false);
                connection.setConnectTimeout(12000);
                connection.setReadTimeout(12000);
                connection.setUseCaches(false);
                connection.setRequestProperty("Connection", "close");
                connection.setRequestProperty("Accept-Encoding", "identity");
                boolean authenticated = clientId != null && !clientId.trim().isEmpty() && uri.getHost().equalsIgnoreCase(initial.getHost());
                connection.setRequestProperty("User-Agent", authenticated && attempt >= 2 ? "v2rayN/7.15.2" : authenticated ? "Happ/4.1.2" : userAgent());
                if (authenticated) {
                    connection.setRequestProperty("X-Hwid", clientId);
                    if (attempt == 0 && platform != null) {
                        connection.setRequestProperty("X-Device-Os", platform.deviceOs());
                        connection.setRequestProperty("X-Device-Model", platform.deviceModel());
                        connection.setRequestProperty("X-Device-Locale", Locale.getDefault().toLanguageTag());
                        connection.setRequestProperty("X-Ver-Os", platform.osVersion());
                    }
                }
                int code = connection.getResponseCode();
                if (code >= 300 && code <= 399) {
                    String location = connection.getHeaderField("Location");
                    if (redirect == 3 || location == null || location.trim().isEmpty()) throw new IOException("Unsafe or excessive redirect.");
                    uri = subscriptionUri(uri.resolve(location.trim()).toString());
                    continue;
                }
                if (code != 200) throw new HttpStatusException(code, connection.getHeaderFields());
                if (connection.getContentLengthLong() > MAX_DOWNLOAD) throw new ResolutionException("Подписка слишком большая.");
                if (inspectHeaders) {
                    ResolutionException headerError = headerError(connection.getHeaderFields(), authenticated, initial.getHost(), false);
                    if (headerError != null) throw headerError;
                }
                byte[] body;
                try (InputStream input = connection.getInputStream()) { body = readLimited(input); }
                if (inspectHeaders && body.length == 0) {
                    if (authenticated && headerTrue(connection.getHeaderFields(), "x-hwid-limit"))
                        throw new DeviceLimitException("Сервис подписки подтвердил лимит устройств и не выдал серверы. Удали старое устройство в кабинете провайдера либо попроси поддержку освободить слот.");
                    if (!authenticated && headerTrue(connection.getHeaderFields(), "x-hwid-not-supported"))
                        throw new IdentityRequiredException(initial.getHost());
                    throw new ResolutionException("Сервис подписки ответил без списка серверов.");
                }
                return body;
            } finally { connection.disconnect(); }
        }
        throw new IOException("Too many redirects.");
    }

    private static ResolutionException headerError(java.util.Map<String, List<String>> headers, boolean authenticated, String host, boolean errorResponse) {
        if (headers == null) return null;
        if (headerTrue(headers, "x-hwid-max-devices-reached"))
            return new DeviceLimitException("Сервис подписки подтвердил: исчерпан лимит устройств. Удали старое устройство в кабинете провайдера либо попроси поддержку освободить слот.");
        if (headerTrue(headers, "x-hwid-not-supported"))
            return authenticated ? new ResolutionException("Сервис не распознал сохранённый ID устройства TRELAY. Провайдер использует несовместимые правила подписки.")
                    : new IdentityRequiredException(host);
        if (authenticated && errorResponse && headerTrue(headers, "x-hwid-limit"))
            return new DeviceLimitException("Сервис подписки отклонил запрос из-за ограничения устройств. Удали старое устройство в кабинете провайдера либо попроси поддержку освободить слот.");
        return null;
    }

    private static boolean headerTrue(java.util.Map<String, List<String>> headers, String name) {
        for (java.util.Map.Entry<String, List<String>> entry : headers.entrySet())
            if (entry.getKey() != null && entry.getKey().equalsIgnoreCase(name) && entry.getValue() != null)
                for (String value : entry.getValue()) if (value != null && value.trim().equalsIgnoreCase("true")) return true;
        return false;
    }

    private static boolean isUnsupportedClientResponse(String link) {
        try {
            Links.Parsed uri = Links.parse(link);
            return ("0.0.0.0".equals(uri.host) && uri.port == 1) || uri.fragment.toLowerCase(Locale.ROOT).contains("not supported");
        } catch (Exception error) { return false; }
    }

    static byte[] readLimited(InputStream input) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = input.read(buffer)) != -1) {
            if (output.size() + read > MAX_DOWNLOAD) throw new IOException("Response is too large.");
            output.write(buffer, 0, read);
        }
        return output.toByteArray();
    }

    private static String userAgent() {
        Platform hooks = platform;
        return hooks == null ? "TRELAY/Android" : hooks.userAgent();
    }

    // ------------------------------------------------------------------ extraction

    static List<String> extractSupportedLinks(String body, String error) throws ResolutionException {
        List<String> links = new ArrayList<>();
        body = (body == null ? "" : body).trim().replace("﻿", "");
        links.addAll(MihomoConfig.extractClash(body));
        links.addAll(MihomoConfig.extractClashProviderUrls(body));
        if (body.indexOf('\n') < 0 && body.indexOf('\r') < 0 && Links.isSupportedLink(body)) links.add(body);
        if (body.startsWith("\"")) {
            try { collectLinks((String) new JSONTokener(body).nextValue(), links); } catch (Exception ignored) { }
        }
        if (body.startsWith("{") || body.startsWith("[")) {
            try {
                Object root = new JSONTokener(body).nextValue();
                links.addAll(MihomoConfig.extractSingBox(root));
                collectJsonLinks(root, links, 0);
                if (links.isEmpty()) links.addAll(MihomoConfig.extractSip008(root));
            } catch (Exception ignored) { }
        }
        for (String line : body.replace("\r", "\n").split("\n")) if (!line.trim().isEmpty()) collectLinks(line, links);
        try {
            String decoded = Links.strictUtf8(Links.base64(body));
            links.addAll(MihomoConfig.extractClash(decoded));
            links.addAll(MihomoConfig.extractClashProviderUrls(decoded));
            if (decoded.startsWith("{") || decoded.startsWith("[")) {
                try {
                    Object parsed = new JSONTokener(decoded).nextValue();
                    links.addAll(MihomoConfig.extractSingBox(parsed));
                    collectJsonLinks(parsed, links, 0);
                } catch (Exception ignored) { }
            }
            for (String line : decoded.replace("\r", "\n").split("\n")) if (!line.trim().isEmpty()) collectLinks(line, links);
        } catch (Exception ignored) { }
        LinkedHashSet<String> clean = new LinkedHashSet<>();
        for (String link : links) {
            String item = link.trim();
            if (Links.isSupportedLink(item) || safeNestedSubscription(item)) clean.add(item);
            if (clean.size() >= 128) break;
        }
        if (clean.isEmpty()) throw new ResolutionException(error);
        return new ArrayList<>(clean);
    }

    private static boolean safeNestedSubscription(String value) {
        try { subscriptionUri(value); return true; } catch (Exception error) { return false; }
    }

    private static void collectLinks(String value, List<String> links) {
        value = value == null ? "" : value.trim();
        if (value.startsWith("\"") && value.endsWith("\"") && value.length() > 1) value = value.substring(1, value.length() - 1);
        if (Links.isSupportedLink(value)) { if (links.size() < 256) links.add(value); return; }
        if (value.length() < 8 || value.length() > MAX_DOWNLOAD || !value.matches("[A-Za-z0-9+/=_\\-\\s]+")) return;
        try {
            String decoded = Links.strictUtf8(Links.base64(value));
            if (decoded.equals(value)) return;
            for (String line : decoded.replace("\r", "\n").split("\n"))
                if (Links.isSupportedLink(line.trim()) && links.size() < 256) links.add(line.trim());
        } catch (Exception ignored) { }
    }

    private static void collectJsonLinks(Object value, List<String> links, int depth) {
        if (value == null || depth > 8 || links.size() >= 256) return;
        if (value instanceof String) { collectLinks((String) value, links); return; }
        if (value instanceof JSONObject) {
            JSONObject object = (JSONObject) value;
            if (XrayConfigs.isEmbeddedCandidate(object)) {
                try { links.add(XrayConfigs.encodeEmbedded(object)); } catch (Exception ignored) { }
                return;
            }
            String generated = linkFromJson(object);
            if (Links.isSupportedLink(generated)) links.add(generated);
            java.util.Iterator<String> keys = object.keys();
            while (keys.hasNext()) collectJsonLinks(object.opt(keys.next()), links, depth + 1);
            return;
        }
        if (value instanceof JSONArray) {
            JSONArray array = (JSONArray) value;
            for (int i = 0; i < array.length(); i++) collectJsonLinks(array.opt(i), links, depth + 1);
        }
    }

    private static String linkFromJson(JSONObject root) {
        for (String name : new String[] {"url", "key", "accessKey", "config"}) {
            Object candidate = root.opt(name);
            if (candidate instanceof String && Links.isSupportedLink((String) candidate)) return ((String) candidate).trim();
        }
        if (root.has("plugin") || root.has("plugin_opts")) return "";
        if (!root.has("server") || !root.has("server_port") || !root.has("method") || !root.has("password")) return "";
        String server = root.optString("server").trim();
        int port;
        try { port = Integer.parseInt(root.optString("server_port").trim()); } catch (Exception error) { return ""; }
        if (server.isEmpty() || port < 1 || port > 65535) return "";
        String credentials = root.optString("method") + ":" + root.optString("password");
        if (server.contains(":")) server = "[" + server + "]";
        return "ss://" + Links.base64Url(credentials.getBytes(StandardCharsets.UTF_8)) + "@" + server + ":" + port;
    }
}
