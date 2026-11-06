package com.trelay.android;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import org.json.JSONArray;
import org.json.JSONObject;

// Configuration shared by UI and engine. The engine always works with a deep copy (snapshot).
final class Model {
    private Model() { }

    static String newId() { return UUID.randomUUID().toString().replace("-", ""); }

    static final class Settings {
        boolean soundAlerts = true;
        String alertOutput = "speaker"; // speaker | system | device:<type>:<name>
        boolean maxAlarmVolume = true;
        boolean vibrate = true;
        boolean autostart = false;

        JSONObject toJson() throws Exception {
            return new JSONObject().put("soundAlerts", soundAlerts).put("alertOutput", alertOutput)
                    .put("maxAlarmVolume", maxAlarmVolume).put("vibrate", vibrate).put("autostart", autostart);
        }

        static Settings fromJson(JSONObject json) {
            Settings settings = new Settings();
            if (json == null) return settings;
            settings.soundAlerts = json.optBoolean("soundAlerts", true);
            settings.alertOutput = json.optString("alertOutput", "speaker");
            settings.maxAlarmVolume = json.optBoolean("maxAlarmVolume", true);
            settings.vibrate = json.optBoolean("vibrate", true);
            settings.autostart = json.optBoolean("autostart", false);
            return settings;
        }
    }

    static final class Server {
        String id = newId();
        String name = "Сервер";
        String link = "";
        String subscription = "";
        boolean subscriptionReplacement;
        boolean singleServerSubscription;
        boolean enabled = true;

        JSONObject toJson() throws Exception {
            return new JSONObject().put("id", id).put("name", name).put("link", link)
                    .put("subscription", subscription).put("subscriptionReplacement", subscriptionReplacement)
                    .put("singleServerSubscription", singleServerSubscription)
                    .put("enabled", enabled);
        }

        static Server fromJson(JSONObject json) throws Exception {
            Server server = new Server();
            server.id = json.optString("id", newId());
            server.name = json.optString("name", "Сервер");
            server.link = json.getString("link");
            server.subscription = json.optString("subscription", "");
            server.subscriptionReplacement = json.optBoolean("subscriptionReplacement", false);
            server.singleServerSubscription = json.optBoolean("singleServerSubscription", false);
            server.enabled = json.optBoolean("enabled", true);
            return server;
        }
    }

    static final class Route {
        String id = newId();
        String name = "Новый маршрут";
        boolean enabled = true;
        boolean telephony = false;
        String healthEndpoint = "";
        final List<String> packages = new ArrayList<>();
        final List<Server> servers = new ArrayList<>();

        List<Server> activeServers() {
            List<Server> result = new ArrayList<>();
            for (Server server : servers) if (server.enabled) result.add(server);
            return result;
        }

        JSONObject toJson() throws Exception {
            JSONArray apps = new JSONArray();
            for (String item : packages) apps.put(item);
            JSONArray list = new JSONArray();
            for (Server server : servers) list.put(server.toJson());
            return new JSONObject().put("id", id).put("name", name).put("enabled", enabled).put("telephony", telephony)
                    .put("healthEndpoint", healthEndpoint).put("packages", apps).put("servers", list);
        }

        static Route fromJsonSafe(Route source) {
            try { return fromJson(new JSONObject(source.toJson().toString())); }
            catch (Exception error) { throw new IllegalStateException(error); }
        }

        static Route fromJson(JSONObject json) throws Exception {
            Route route = new Route();
            route.id = json.optString("id", newId());
            route.name = json.optString("name", "Маршрут");
            route.enabled = json.optBoolean("enabled", true);
            route.telephony = json.optBoolean("telephony", false);
            route.healthEndpoint = json.optString("healthEndpoint", "");
            JSONArray apps = json.optJSONArray("packages");
            if (apps != null) for (int i = 0; i < apps.length(); i++) if (!apps.optString(i).isEmpty()) route.packages.add(apps.optString(i));
            JSONArray list = json.optJSONArray("servers");
            if (list != null) for (int i = 0; i < list.length(); i++) route.servers.add(Server.fromJson(list.getJSONObject(i)));
            return route;
        }
    }

    static final class Config {
        Settings settings = new Settings();
        final List<Route> routes = new ArrayList<>();

        JSONObject toJson() throws Exception {
            JSONArray list = new JSONArray();
            for (Route route : routes) list.put(route.toJson());
            return new JSONObject().put("version", 3).put("settings", settings.toJson()).put("routes", list);
        }

        static Config fromJson(JSONObject json) throws Exception {
            Config config = new Config();
            config.settings = Settings.fromJson(json.optJSONObject("settings"));
            JSONArray list = json.optJSONArray("routes");
            if (list != null) for (int i = 0; i < list.length(); i++) config.routes.add(Route.fromJson(list.getJSONObject(i)));
            return config;
        }

        Config copy() {
            try { return fromJson(new JSONObject(toJson().toString())); }
            catch (Exception error) { throw new IllegalStateException(error); }
        }

        Route route(String id) {
            for (Route route : routes) if (route.id.equals(id)) return route;
            return null;
        }

        /** Route that owns the package (first enabled route in priority order). */
        Route routeForPackage(String packageName) {
            for (Route route : routes) if (route.enabled && route.packages.contains(packageName)) return route;
            return null;
        }
    }
}
