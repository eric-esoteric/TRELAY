package com.trelay.android;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.json.JSONObject;

final class SubscriptionUpdates {
    private static final int MAX_REPLACEMENTS = 8;

    private SubscriptionUpdates() { }

    static String connectionKey(String link) {
        String value = link.trim();
        try {
            if (Links.scheme(value).equals("vmess")) {
                JSONObject config = new JSONObject(Links.strictUtf8(Links.base64(value.substring("vmess://".length()))));
                config.remove("ps");
                return "vmess:" + config;
            }
            if (value.startsWith(XrayConfigs.PREFIX)) {
                JSONObject config = XrayConfigs.decodeEmbedded(value);
                config.remove("remarks");
                return XrayConfigs.encodeEmbedded(config);
            }
        } catch (Exception ignored) {
            // Use the original string when a legacy value cannot be decoded.
        }
        int fragment = value.indexOf('#');
        return fragment >= 0 && !Links.isDynamic(value) && !Links.scheme(value).equals("https")
                ? value.substring(0, fragment) : value;
    }

    static List<String> candidates(List<String> links) {
        Map<String, String> valid = new LinkedHashMap<>();
        for (String link : links) {
            try {
                if (Links.isDynamic(link) || Links.scheme(link).equals("https")) continue;
                Links.validate(link);
                valid.putIfAbsent(connectionKey(link), link);
            } catch (IllegalArgumentException ignored) {
                // A malformed entry cannot be staged as a replacement.
            }
        }
        return new ArrayList<>(valid.values());
    }

    static int stage(Model.Route route, String source, List<String> candidates) {
        List<Model.Server> selected = new ArrayList<>();
        Map<String, Model.Server> known = new LinkedHashMap<>();
        int replacements = 0;
        for (Model.Server server : route.servers) {
            known.put(connectionKey(server.link), server);
            if (!source.equals(server.subscription)) continue;
            if (server.subscriptionReplacement) replacements++;
            else selected.add(server);
        }
        int added = 0;
        Map<Model.Server, String> proposed = new LinkedHashMap<>();
        Map<String, Integer> matches = new LinkedHashMap<>();
        for (Model.Server server : selected) {
            if (!server.enabled || contains(candidates, server.link) || replacements >= MAX_REPLACEMENTS) continue;
            String candidate = replacement(server.link, server.singleServerSubscription, candidates);
            if (candidate == null || known.containsKey(connectionKey(candidate))) continue;
            proposed.put(server, candidate);
            matches.merge(connectionKey(candidate), 1, Integer::sum);
        }
        for (Map.Entry<Model.Server, String> proposal : proposed.entrySet()) {
            Model.Server server = proposal.getKey();
            String candidate = proposal.getValue();
            if (matches.get(connectionKey(candidate)) != 1 || replacements >= MAX_REPLACEMENTS) continue;
            Model.Server reserve = new Model.Server();
            reserve.name = server.name + " · из подписки";
            reserve.link = candidate;
            reserve.subscription = source;
            reserve.subscriptionReplacement = true;
            route.servers.add(route.servers.indexOf(server) + 1, reserve);
            known.put(connectionKey(candidate), reserve);
            replacements++;
            added++;
        }
        return added;
    }

    static String replacement(String previous, boolean onlySelected, List<String> candidates) {
        if (onlySelected && candidates.size() == 1) return candidates.get(0);
        List<String> matches = new ArrayList<>();
        String kind = Links.kindLabel(previous);
        String name = Links.suggestName(previous);
        String endpoint = Links.endpoint(previous);
        for (String candidate : candidates) {
            if (!kind.equals(Links.kindLabel(candidate))) continue;
            if ((!name.isEmpty() && name.equalsIgnoreCase(Links.suggestName(candidate))) ||
                    (!endpoint.equals("сервер") && endpoint.equalsIgnoreCase(Links.endpoint(candidate)))) {
                matches.add(candidate);
            }
        }
        return matches.size() == 1 ? matches.get(0) : null;
    }

    private static boolean contains(List<String> candidates, String link) {
        String key = connectionKey(link);
        for (String candidate : candidates) if (key.equals(connectionKey(candidate))) return true;
        return false;
    }
}
