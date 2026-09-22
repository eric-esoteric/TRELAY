package com.trelay.android;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.net.ConnectivityManager;
import android.net.VpnService;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelFileDescriptor;
import android.os.PowerManager;
import android.system.OsConstants;
import java.io.File;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;
import libv2ray.ProcessFinder;

public final class TrelayVpnService extends VpnService implements TunnelEngine.Listener {
    static final String START = "com.trelay.android.START_VPN";
    static final String APPLY = "com.trelay.android.APPLY_CONFIG";
    static final String MANUAL_REFRESH = "com.trelay.android.MANUAL_REFRESH";
    static final String STOP = "com.trelay.android.STOP_VPN";
    static final String STATE_CHANGED = "com.trelay.android.VPN_STATE_CHANGED";
    private static final String CHANNEL_VPN = "trelay_vpn";
    static final String CHANNEL_ALERTS = "trelay_alerts";
    private static final int NOTIFICATION_ID = 17;

    private static volatile TrelayVpnService running;

    private final Handler main = new Handler(Looper.getMainLooper());
    private final AtomicInteger alertIds = new AtomicInteger(1000);
    private final Object routerLock = new Object();
    private TunnelEngine engine;
    private volatile Model.Config config = new Model.Config();
    private ParcelFileDescriptor tun;
    private String tunPackages = "";
    private XrayCore.Instance front;
    private PowerManager.WakeLock wakeLock;
    private boolean broadcastPending;
    private ConnectivityManager.NetworkCallback networkCallback;
    private volatile boolean stopping;

    static TunnelEngine engine() { TrelayVpnService service = running; return service == null ? null : service.engine; }

    static boolean isRunning() {
        TunnelEngine current = engine();
        return current != null && current.state() != TunnelEngine.State.OFF;
    }

    static void start(Context context) {
        context.startForegroundService(new Intent(context, TrelayVpnService.class).setAction(START));
    }

    static void stop(Context context) {
        if (running == null) return;
        context.startService(new Intent(context, TrelayVpnService.class).setAction(STOP));
    }

    /** Applies a saved configuration to a running VPN without interrupting unchanged routes. */
    static void applyIfRunning(Context context) {
        if (running == null) return;
        context.startService(new Intent(context, TrelayVpnService.class).setAction(APPLY));
    }

    static boolean manualRefresh(Context context) {
        TrelayVpnService service = running;
        if (service == null || service.engine == null) return false;
        context.startService(new Intent(context, TrelayVpnService.class).setAction(MANUAL_REFRESH));
        return true;
    }

    @Override public void onCreate() {
        super.onCreate();
        TrelayApp.init(this);
        createChannels(this);
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? START : intent.getAction();
        if (STOP.equals(action)) { shutdown(); return START_NOT_STICKY; }
        if (MANUAL_REFRESH.equals(action) && engine != null) {
            keepAwakeFor(15000);
            engine.manualRefresh();
            SipPeer.reregisterNow(this, config, engine);
            publish();
            return START_STICKY;
        }
        if (Watchdog.ACTION_CHECK.equals(action) && engine != null) {
            // Periodic check from the watchdog alarm: wake the probes and keep the SIP app's picture fresh.
            engine.nudge();
            SipPeer.publish(this, config, engine);
            return START_STICKY;
        }
        if (VpnService.prepare(this) != null) {
            EngineLog.write("START_REJECTED vpn permission missing");
            stopSelf();
            return START_NOT_STICKY;
        }
        startInForeground("Запускаю маршруты…");
        try { config = new ConfigStore(this).load(); }
        catch (Exception error) {
            EngineLog.write("CONFIG_LOAD_FAILED " + error.getClass().getSimpleName());
            showAlert(new TunnelEngine.Alert(TunnelEngine.AlertKind.ROUTE_LOST, false, "TRELAY не запущен", "Зашифрованное хранилище недоступно."));
            shutdown();
            return START_NOT_STICKY;
        }
        if (engine == null) {
            stopping = false;
            engine = new TunnelEngine(new AndroidPlatform());
            engine.addListener(this);
            running = this;
            main.removeCallbacks(peerHeartbeat);
            main.postDelayed(peerHeartbeat, 60_000L);
            purgeRuntime();
            watchNetwork();
            EngineLog.write("SERVICE_STARTED " + XrayCore.version());
        }
        engine.start(config);
        Watchdog.schedule(this);
        publish();
        return START_STICKY;
    }

    private void startInForeground(String text) {
        Notification notification = notification(text);
        if (Build.VERSION.SDK_INT >= 34) startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
        else startForeground(NOTIFICATION_ID, notification);
    }

    private void shutdown() {
        stopping = true;
        TunnelEngine current = engine;
        engine = null;
        running = null;
        new Thread(() -> {
            if (current != null) { current.stop(); current.dispose(); }
            synchronized (routerLock) { stopRouterLocked(); }
            main.post(() -> {
                publish();
                if (engine != null) return; // started again while shutting down
                releaseWakeLock();
                unwatchNetwork();
                main.removeCallbacks(peerHeartbeat);
                SipPeer.offline(TrelayVpnService.this, config);
                Watchdog.cancel(TrelayVpnService.this);
                stopForeground(STOP_FOREGROUND_REMOVE);
                stopSelf();
            });
        }, "trelay-shutdown").start();
    }

    @Override public void onRevoke() {
        EngineLog.write("VPN_REVOKED");
        if (!stopping) showAlert(new TunnelEngine.Alert(TunnelEngine.AlertKind.ROUTE_LOST, true, "TRELAY: VPN отключён",
                "Android отключил VPN TRELAY (другое VPN-приложение или системная настройка). Приложения маршрутов теперь идут без туннеля."));
        shutdown();
    }

    @Override public void onDestroy() {
        if (engine != null) shutdown();
        super.onDestroy();
    }

    // ------------------------------------------------------------------ engine events

    @Override public void stateChanged() { main.post(this::publish); }

    @Override public void alert(TunnelEngine.Alert alert) { main.post(() -> showAlert(alert)); }

    // The SIP app shows what TRELAY reports, and treats a long silence as "TRELAY is gone". While the
    // phone is awake, repeat the state every minute so its status line never goes stale by accident.
    private final Runnable peerHeartbeat = new Runnable() {
        @Override public void run() {
            TunnelEngine current = engine;
            if (current == null || stopping) return;
            SipPeer.publish(TrelayVpnService.this, config, current);
            main.postDelayed(this, 60_000L);
        }
    };

    private void publish() {
        TunnelEngine current = engine;
        if (current != null && !stopping) {
            getSystemService(NotificationManager.class).notify(NOTIFICATION_ID, notification(stateTitle(current.state()) + " · " + current.statusText()));
            // The SIP app cannot see a failover by itself: tell it, so it re-registers at once.
            SipPeer.publish(this, config, current);
        }
        if (broadcastPending) return;
        broadcastPending = true;
        main.postDelayed(() -> {
            broadcastPending = false;
            sendBroadcast(new Intent(STATE_CHANGED).setPackage(getPackageName()));
        }, 200);
    }

    static String stateTitle(TunnelEngine.State state) {
        switch (state) {
            case STARTING: return "Проверка";
            case READY: return "Работает";
            case ERROR: return "Проблема";
            case STOPPING: return "Отключение";
            default: return "Выключено";
        }
    }

    private void showAlert(TunnelEngine.Alert alert) {
        Model.Settings settings = config.settings;
        Intent open = new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent content = PendingIntent.getActivity(this, 2, open, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification notification = new Notification.Builder(this, CHANNEL_ALERTS).setSmallIcon(R.drawable.ic_stat_trelay)
                .setContentTitle(alert.title).setContentText(alert.message).setStyle(new Notification.BigTextStyle().bigText(alert.message))
                .setContentIntent(content).setAutoCancel(true).setCategory(alert.critical ? Notification.CATEGORY_ALARM : Notification.CATEGORY_STATUS)
                .build();
        try { getSystemService(NotificationManager.class).notify(alertIds.incrementAndGet(), notification); } catch (Exception ignored) { }
        // Windows 0.2.1: only confirmed serious problems are loud; recovery stays silent.
        if (settings.soundAlerts && alert.critical) AlertSound.play(this, true, settings, false);
    }

    private Notification notification(String text) {
        Intent open = new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent content = PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent stopAction = PendingIntent.getService(this, 1, new Intent(this, TrelayVpnService.class).setAction(STOP),
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this, CHANNEL_VPN).setSmallIcon(R.drawable.ic_stat_trelay)
                .setContentTitle("TRELAY").setContentText(text).setStyle(new Notification.BigTextStyle().bigText(text))
                .setContentIntent(content).addAction(new Notification.Action.Builder(null, "Отключить", stopAction).build())
                .setOngoing(true).setOnlyAlertOnce(true).build();
    }

    static void createChannels(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        NotificationChannel vpn = new NotificationChannel(CHANNEL_VPN, "Состояние VPN", NotificationManager.IMPORTANCE_LOW);
        vpn.setShowBadge(false);
        manager.createNotificationChannel(vpn);
        NotificationChannel alerts = new NotificationChannel(CHANNEL_ALERTS, "Оповещения о связи", NotificationManager.IMPORTANCE_HIGH);
        alerts.setSound(null, null); // TRELAY plays its own alarm on the chosen output
        alerts.enableVibration(false);
        alerts.setBypassDnd(true);
        manager.createNotificationChannel(alerts);
    }

    // Wi-Fi <-> mobile switches kill tunnels that were bound to the old network: probe at once.
    private void watchNetwork() {
        if (networkCallback != null) return;
        ConnectivityManager connectivity = getSystemService(ConnectivityManager.class);
        networkCallback = new ConnectivityManager.NetworkCallback() {
            @Override public void onAvailable(android.net.Network network) { nudgeEngine("NETWORK_AVAILABLE"); }
            @Override public void onLost(android.net.Network network) { nudgeEngine("NETWORK_LOST"); }
        };
        try { connectivity.registerDefaultNetworkCallback(networkCallback); } catch (Exception error) { networkCallback = null; }
    }

    private void unwatchNetwork() {
        if (networkCallback == null) return;
        try { getSystemService(ConnectivityManager.class).unregisterNetworkCallback(networkCallback); } catch (Exception ignored) { }
        networkCallback = null;
    }

    private void nudgeEngine(String reason) {
        TunnelEngine current = engine;
        if (current == null) return;
        EngineLog.write(reason);
        keepAwakeFor(10000);
        current.nudge();
    }

    /** Short, self-releasing wake lock: failover must finish even if the screen is off. */
    private void keepAwakeFor(long ms) {
        synchronized (routerLock) {
            if (wakeLock == null) {
                wakeLock = getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "TRELAY:failover");
                wakeLock.setReferenceCounted(false);
            }
            wakeLock.acquire(Math.max(1000, Math.min(ms, 30000)));
        }
    }

    private void releaseWakeLock() {
        if (wakeLock != null) { try { wakeLock.release(); } catch (Exception ignored) { } wakeLock = null; }
    }

    private void purgeRuntime() {
        File[] files = new File(getFilesDir(), "runtime").listFiles();
        if (files != null) for (File file : files) file.delete();
    }

    private void stopRouterLocked() {
        if (front != null) { front.stop(); front = null; }
        if (tun != null) { try { tun.close(); } catch (Exception ignored) { } tun = null; }
        tunPackages = "";
    }

    // ------------------------------------------------------------------ platform for the engine

    private final class AndroidPlatform implements TunnelEngine.Platform {
        @Override public TunnelEngine.Runner startBackend(String serverId, String key, int port) throws Exception {
            if (Links.engine(key).equals("mihomo")) return MihomoRunner.start(TrelayVpnService.this, serverId, key, port);
            return XrayCore.start(TrelayVpnService.this, XrayConfigs.backend(key, port).toString(), 0, null);
        }

        @Override public void applyRouter(List<TunnelEngine.RouterGroup> groups, int dnsPort) throws Exception {
            synchronized (routerLock) {
                if (stopping) throw new IllegalStateException("VPN останавливается.");
                PackageManager packages = getPackageManager();
                List<XrayConfigs.FrontGroup> frontGroups = new ArrayList<>();
                List<String> installed = new ArrayList<>();
                int index = 0;
                for (TunnelEngine.RouterGroup group : groups) {
                    List<Integer> uids = new ArrayList<>();
                    for (String name : group.packages) {
                        if (name.equals(getPackageName())) continue;
                        try {
                            uids.add(packages.getApplicationInfo(name, 0).uid);
                            installed.add(name);
                        } catch (PackageManager.NameNotFoundException missing) {
                            EngineLog.write("PACKAGE_MISSING " + name);
                        }
                    }
                    frontGroups.add(new XrayConfigs.FrontGroup("route-" + index++, uids, group.relayPort));
                }
                if (installed.isEmpty()) throw new IllegalArgumentException("Выбранные приложения не установлены на телефоне.");
                java.util.Collections.sort(installed);
                String signature = String.join(",", installed);
                ParcelFileDescriptor previous = null;
                if (tun == null || !signature.equals(tunPackages)) {
                    Builder builder = new Builder().setSession("TRELAY").setMtu(1500)
                            .addAddress(XrayConfigs.TUN_ADDRESS, 30).addRoute("0.0.0.0", 0)
                            .addAddress(XrayConfigs.TUN_ADDRESS6, 126).addRoute("::", 0)
                            .addDnsServer(XrayConfigs.TUN_DNS).setBlocking(false).setMetered(false);
                    for (String name : installed) builder.addAllowedApplication(name);
                    PendingIntent open = PendingIntent.getActivity(TrelayVpnService.this, 3, new Intent(TrelayVpnService.this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE);
                    builder.setConfigureIntent(open);
                    ParcelFileDescriptor created = builder.establish();
                    if (created == null) throw new IllegalStateException("Android не разрешил VPN для TRELAY.");
                    previous = tun;
                    tun = created;
                    tunPackages = signature;
                }
                if (front != null) { front.stop(); front = null; }
                if (previous != null) { try { previous.close(); } catch (Exception ignored) { } }
                String frontConfig = XrayConfigs.front(frontGroups, dnsPort, 1500).toString();
                front = XrayCore.start(TrelayVpnService.this, frontConfig, tun.getFd(), groups.size() > 1 ? new OwnerFinder() : null);
                EngineLog.write("ROUTER_APPLIED apps=" + installed.size() + " routes=" + groups.size());
            }
        }

        @Override public boolean routerAlive() {
            synchronized (routerLock) { return tun != null && front != null && front.alive(); }
        }

        @Override public void stopRouter() {
            synchronized (routerLock) { stopRouterLocked(); }
        }

        @Override public boolean interactive() {
            PowerManager power = getSystemService(PowerManager.class);
            return power != null && power.isInteractive();
        }

        @Override public void keepAwake(long ms) { keepAwakeFor(ms); }

        @Override public void log(String line) { EngineLog.write(line); }
    }

    // Xray asks for the owner of every new connection when several routes exist.
    private final class OwnerFinder implements ProcessFinder {
        private final Map<String, long[]> cache = new LinkedHashMap<String, long[]>(128, 0.75f, true) {
            @Override protected boolean removeEldestEntry(Map.Entry<String, long[]> eldest) { return size() > 1024; }
        };

        @Override public long findProcessByConnection(String network, String srcIp, long srcPort, String dstIp, long dstPort) {
            String key = network + "|" + srcIp + "|" + srcPort + "|" + dstIp + "|" + dstPort;
            long now = System.currentTimeMillis();
            synchronized (cache) {
                long[] hit = cache.get(key);
                if (hit != null && now - hit[1] < 10000) return hit[0];
            }
            int uid = -1;
            try {
                ConnectivityManager connectivity = getSystemService(ConnectivityManager.class);
                int protocol = "tcp".equals(network) ? OsConstants.IPPROTO_TCP : OsConstants.IPPROTO_UDP;
                InetSocketAddress local = new InetSocketAddress(InetAddress.getByName(srcIp), (int) srcPort);
                InetSocketAddress remote = new InetSocketAddress(InetAddress.getByName(dstIp), (int) dstPort);
                uid = connectivity.getConnectionOwnerUid(protocol, local, remote);
                if (uid < 0 && protocol == OsConstants.IPPROTO_UDP) {
                    // Unconnected UDP sockets are bound to a wildcard address.
                    boolean v6 = srcIp.contains(":");
                    InetSocketAddress wildcard = new InetSocketAddress(InetAddress.getByName(v6 ? "::" : "0.0.0.0"), (int) srcPort);
                    uid = connectivity.getConnectionOwnerUid(protocol, wildcard, remote);
                }
            } catch (Exception error) {
                uid = -1;
            }
            synchronized (cache) { cache.put(key, new long[] {uid, now}); }
            return uid;
        }
    }
}

