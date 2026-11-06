package com.trelay.android;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

// Tells the SIP application (TLINE) what its telephony route is doing.
//
// A failover changes the public address of the phone without changing anything Android can see: the
// TUN interface and its address stay put, so the SIP client has no reason to re-register and the PBX
// keeps sending calls to the server that just died. TRELAY therefore says it out loud, and TLINE
// re-registers at once.
final class SipPeer {
    static final String PACKAGE = "com.tline.phone";
    static final String ACTION_FAILOVER = PACKAGE + ".TRELAY_FAILOVER";
    static final String ACTION_STATE = PACKAGE + ".TRELAY_STATE";
    // The SIP app treats silence longer than five minutes as "TRELAY is gone", so speak up well
    // before that: a broadcast a minute costs nothing and keeps its status line honest.
    private static final long HEARTBEAT_MS = 60 * 1000L;

    private static final Map<String, String> lastServerId = new HashMap<>();
    private static final Map<String, String> lastText = new HashMap<>();
    private static long lastSentAt;

    private SipPeer() { }

    /** Sends a signal whenever the route carrying the SIP app changes, and a heartbeat in between. */
    static void publish(Context context, Model.Config config, TunnelEngine engine) {
        publish(context, config, engine, false);
    }

    /** With force, answers even when nothing changed: the SIP app has just started and knows nothing. */
    static void publish(Context context, Model.Config config, TunnelEngine engine, boolean force) {
        if (context == null || config == null || engine == null) return;
        Model.Route route = routeOf(config);
        if (route == null) return;
        TunnelEngine.RouteInfo info = engine.routeInfo().get(route.id);
        if (info == null) return;
        String serverId = info.activeServerId;
        String serverName = nameOf(route, serverId);
        String previousId = lastServerId.get(route.id);
        boolean moved = previousId != null && serverId != null && !previousId.equals(serverId);
        boolean changed = moved || !equal(previousId, serverId) || !equal(lastText.get(route.id), info.text);
        long now = System.currentTimeMillis();
        if (!force && !changed && now - lastSentAt < HEARTBEAT_MS) return;
        lastServerId.put(route.id, serverId);
        lastText.put(route.id, info.text);
        lastSentAt = now;
        Intent intent = new Intent(moved ? ACTION_FAILOVER : ACTION_STATE)
                .putExtra("route", route.name)
                .putExtra("server", serverName)
                .putExtra("up", info.working)
                .putExtra("detail", info.text);
        if (moved) intent.putExtra("from", nameOf(route, previousId));
        send(context, intent);
        EngineLog.write((moved ? "SIP_PEER_FAILOVER " : "SIP_PEER_STATE ") + PACKAGE + " up=" + info.working);
    }

    /** A deliberate pull-to-refresh: retry REGISTER now even when the selected server did not change. */
    static void reregisterNow(Context context, Model.Config config, TunnelEngine engine) {
        if (context == null || config == null || engine == null) return;
        Model.Route route = routeOf(config);
        if (route == null) return;
        TunnelEngine.RouteInfo info = engine.routeInfo().get(route.id);
        if (info == null) return;
        String serverId = info.activeServerId;
        String serverName = nameOf(route, serverId);
        lastServerId.put(route.id, serverId);
        lastText.put(route.id, info.text);
        lastSentAt = System.currentTimeMillis();
        send(context, new Intent(ACTION_FAILOVER)
                .putExtra("route", route.name)
                .putExtra("server", serverName)
                .putExtra("from", serverName)
                .putExtra("up", info.working)
                .putExtra("detail", info.text)
                .putExtra("manual", true));
        EngineLog.write("SIP_PEER_MANUAL_REREGISTER " + PACKAGE + " up=" + info.working);
    }

    /** The VPN is going away: the SIP app should know that its route is no longer carrying anything. */
    static void offline(Context context, Model.Config config) {
        if (context == null || config == null) return;
        Model.Route route = routeOf(config);
        if (route == null) return;
        lastServerId.remove(route.id);
        lastText.remove(route.id);
        lastSentAt = 0;
        send(context, new Intent(ACTION_STATE)
                .putExtra("route", route.name)
                .putExtra("server", "")
                .putExtra("up", false)
                .putExtra("detail", "VPN выключен"));
    }

    private static void send(Context context, Intent intent) {
        try {
            // Deliver only to TLINE. The receiver requires the shared signing identity.
            // FLAG_INCLUDE_STOPPED_PACKAGES: a SIP client that Android has killed must still hear this.
            intent.setComponent(new ComponentName(PACKAGE, PACKAGE + ".TrelayReceiver"))
                    .addFlags(Intent.FLAG_INCLUDE_STOPPED_PACKAGES | Intent.FLAG_RECEIVER_FOREGROUND);
            context.sendBroadcast(intent);
        } catch (Exception error) {
            EngineLog.write("SIP_PEER_FAILED " + error.getClass().getSimpleName());
        }
    }

    /** The route that carries the SIP app; a telephony route wins when the app is in several. */
    private static Model.Route routeOf(Model.Config config) {
        Model.Route fallback = null;
        for (Model.Route route : config.routes) {
            if (!route.enabled || !route.packages.contains(PACKAGE)) continue;
            if (route.telephony) return route;
            if (fallback == null) fallback = route;
        }
        return fallback;
    }

    private static String nameOf(Model.Route route, String serverId) {
        if (serverId == null) return "";
        List<Model.Server> servers = route.servers;
        for (Model.Server server : servers) if (serverId.equals(server.id)) return server.name;
        return "";
    }

    private static boolean equal(String left, String right) {
        return left == null ? right == null : left.equals(right);
    }
}

