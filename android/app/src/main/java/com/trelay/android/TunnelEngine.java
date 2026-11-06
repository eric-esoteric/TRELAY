package com.trelay.android;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;

// Port of Windows TunnelEngine.
//  * every enabled server runs as its own warm backend (Xray or Mihomo) with an independent
//    lifecycle thread: start, health probes, restart with backoff;
//  * every route (group of applications) gets a stable loopback SOCKS relay; the TUN router points
//    at the relays and is restarted only when the application mapping changes;
//  * the worker thread only decides which backend serves each route, when to fail over and when
//    to return to a recovered higher-priority server.
final class TunnelEngine {
    // Phone power profile. Windows probes the active server every 1.5 s; on a phone that would keep the
    // radio awake all day. Probes are fast while the route carries traffic (a call), relaxed when the
    // screen is on and sparse when the phone sleeps. Relay failures trigger an immediate probe anyway.
    static final int BUSY_PROBE_INTERVAL_MS = 2000;
    static final int SIP_PROBE_INTERVAL_MS = 10000;
    static final int ACTIVE_PROBE_INTERVAL_MS = Integer.getInteger("trelay.activeProbeMs", 5000);
    static final int IDLE_ACTIVE_PROBE_INTERVAL_MS = 30000;
    static final int IDLE_STANDBY_PROBE_INTERVAL_MS = 120000;
    static final int WARM_SERVERS = Integer.getInteger("trelay.warmServers", 2);
    static final long COOL_DOWN_AFTER = 300_000;
    static final long BUSY_WINDOW = 15_000;
    static final int CONFIRM_PROBE_DELAY_MS = 250;
    static final int STANDBY_PROBE_INTERVAL_MS = Integer.getInteger("trelay.standbyProbeMs", 20000);
    static final int STARTING_PROBE_INTERVAL_MS = 1000;
    static final int ACTIVE_PROBE_TIMEOUT_MS = 2500;
    static final int STANDBY_PROBE_TIMEOUT_MS = 5000;
    static final int CONFIRM_PROBE_TIMEOUT_MS = 1200;
    static final int FAILURE_THRESHOLD = 2;
    static final int STARTING_FAILURE_THRESHOLD = 3;
    static final int RECOVERY_SUCCESSES = 2;
    static final long FRESH_BACKUP_WINDOW = 12_000;
    static final long RECOVERY_HOLD = Long.getLong("trelay.recoveryHoldMs", 120_000L);
    static final long ROUTE_LOSS_ALERT_HOLD = 5_000;
    static final long STARTUP_GRACE = 12_000;
    static final long APP_ALERT_QUIET_AFTER_ROUTER_START = 15_000;
    static final long APP_ALERT_QUIET_AFTER_FAILOVER = 30_000;

    enum State { OFF, STARTING, READY, ERROR, STOPPING }
    enum ServerStatus { STARTING, ACTIVE, STANDBY, COLD, UNHEALTHY, DOWN, SKIPPED }
    enum AlertKind { APP_DISCONNECTED, APP_RECONNECTED, FAILOVER, RECOVERY, ROUTE_LOST, ROUTE_RESTORED, ROUTER_RESTARTED }
    private enum Health { STARTING, HEALTHY, UNHEALTHY, DOWN }

    static final class Alert {
        final AlertKind kind; final boolean critical; final String title; final String message;
        Alert(AlertKind kind, boolean critical, String title, String message) { this.kind = kind; this.critical = critical; this.title = title; this.message = message; }
    }

    static final class ServerInfo {
        final ServerStatus status; final String detail;
        ServerInfo(ServerStatus status, String detail) { this.status = status; this.detail = detail; }
    }

    static final class RouteInfo {
        final boolean working; final String activeServerId; final String text;
        RouteInfo(boolean working, String activeServerId, String text) { this.working = working; this.activeServerId = activeServerId; this.text = text; }
        @Override public String toString() { return working + "|" + activeServerId + "|" + text; }
    }

    static final class RouterGroup {
        final String routeId; final List<String> packages; final int relayPort;
        RouterGroup(String routeId, List<String> packages, int relayPort) { this.routeId = routeId; this.packages = packages; this.relayPort = relayPort; }
    }

    interface Runner { boolean alive(); void stop(); }

    interface Platform {
        /** Starts one engine instance with a loopback SOCKS inbound on the given port. */
        Runner startBackend(String serverId, String resolvedKey, int port) throws Exception;
        /** (Re)starts the TUN router. Packages of each group are routed to its relay port; port 53 goes to dnsPort. */
        void applyRouter(List<RouterGroup> groups, int dnsPort) throws Exception;
        boolean routerAlive();
        void stopRouter();
        /** True while the screen is on. */
        boolean interactive();
        /** Keeps the CPU awake for a short confirmation / failover window. */
        void keepAwake(long ms);
        void log(String line);
    }

    interface Listener { void stateChanged(); void alert(Alert alert); }

    static final class ConfigurationException extends Exception { ConfigurationException(String message) { super(message); } }

    private final class Backend {
        final String id;
        final String fingerprint;
        final RouteRelay.Target target;
        final Semaphore probeSignal = new Semaphore(0);
        final Object gate = new Object();
        volatile boolean cancelled;
        Model.Server server;
        Model.Route route;
        Runner runner;
        Health health = Health.STARTING;
        int failures, successes;
        long healthySince, lastFailure, firstFailure, lastSuccess;
        String detail = "запуск";
        volatile boolean active;
        volatile boolean warm;
        volatile long warmedAt;
        volatile String learnedSipTarget = "";
        final AtomicLong lastProbeRequest = new AtomicLong();
        int priority;

        Backend(Model.Route route, Model.Server server, String fingerprint) {
            id = server.id; this.route = route; this.server = server; this.fingerprint = fingerprint;
            target = new RouteRelay.Target(server.id, server.name);
        }
        String name() { synchronized (gate) { return server.name; } }
        int failuresNow() { synchronized (gate) { return failures; } }
        Health health() { synchronized (gate) { return health; } }
        void signal() { if (probeSignal.availablePermits() == 0) probeSignal.release(); }
    }

    private final class Group implements RouteRelay.Listener {
        final String routeId;
        String label;
        boolean telephony;
        List<String> packages = new ArrayList<>();
        volatile List<Backend> candidates = new ArrayList<>();
        volatile String learnedSipTarget = "";
        RouteRelay relay;
        volatile Backend active;
        volatile RouteRelay.Target[] targets = new RouteRelay.Target[0];
        final long createdAt = System.currentTimeMillis();
        boolean lostAlerted;
        long lostSince, lastFailover;
        Group(String routeId) { this.routeId = routeId; }

        @Override public RouteRelay.Target[] targets() { return targets; }
        @Override public void failure(RouteRelay.Target target, RouteRelay.Failure kind) { onRelayFailure(target, kind); }
        @Override public void closed(RouteRelay.Closed info) { onLongConnectionClosed(this, info); }
        @Override public void sipTarget(String host, int port, boolean udp) { onSipTarget(this, host, port, udp); }
        @Override public void log(String line) { TunnelEngine.this.log(line); }
    }

    private final Platform platform;
    private final CopyOnWriteArrayList<Listener> listeners = new CopyOnWriteArrayList<>();
    private final Object sync = new Object();
    private final Object wakeLock = new Object();
    private boolean wakeFlag;
    private final ExecutorService probes = Executors.newCachedThreadPool(r -> { Thread t = new Thread(r, "trelay-probe"); t.setDaemon(true); return t; });

    // Owned by the worker thread.
    private final Map<String, Backend> backends = new LinkedHashMap<>();
    private final Map<String, Group> groups = new LinkedHashMap<>();
    private final List<String> skipped = new ArrayList<>();
    private final Map<String, String> skippedServers = new HashMap<>();
    private final Set<Integer> reservedPorts = Collections.synchronizedSet(new HashSet<>());
    private volatile Map<String, Backend> backendIndex = new HashMap<>();
    private volatile List<Group> groupOrder = new ArrayList<>();
    private Model.Config config;
    private String routerMapping;
    private volatile boolean routerRunning;
    private final List<Long> routerCrashes = new ArrayList<>();
    private long nextRouterStart, lastRouterAlert;
    private final AtomicLong routerStartedAt = new AtomicLong();
    private boolean runtimeActive;
    private DnsForwarder dns;
    private final Map<String, Long> appAlerted = new HashMap<>();
    private final Map<String, Long> routeFailoverAt = new HashMap<>();

    // Shared with the UI under sync.
    private Thread worker;
    private volatile boolean disposed;
    private Model.Config requested;
    private int requestGeneration, adoptedGeneration = -1;
    private State state = State.OFF;
    private String statusText = "VPN выключен";
    private Map<String, ServerInfo> serverInfo = new HashMap<>();
    private Map<String, RouteInfo> routeInfo = new HashMap<>();

    TunnelEngine(Platform platform) { this.platform = platform; }

    void addListener(Listener listener) { listeners.add(listener); }
    void removeListener(Listener listener) { listeners.remove(listener); }

    State state() { synchronized (sync) { return state; } }
    boolean routerReady() { return routerRunning; }
    String statusText() { synchronized (sync) { return statusText; } }
    Map<String, ServerInfo> serverInfo() { synchronized (sync) { return new HashMap<>(serverInfo); } }
    Map<String, RouteInfo> routeInfo() { synchronized (sync) { return new HashMap<>(routeInfo); } }

    void start(Model.Config newConfig) {
        Model.Config snapshot = newConfig.copy();
        synchronized (sync) {
            if (disposed) throw new IllegalStateException("engine disposed");
            boolean wasRunning = requested != null && state != State.OFF && state != State.STOPPING;
            requested = snapshot;
            requestGeneration++;
            if (!wasRunning) { state = State.STARTING; statusText = "Запускаю маршруты…"; }
            if (worker == null) {
                worker = new Thread(this::workerLoop, "trelay-engine");
                worker.setDaemon(true);
                worker.start();
            }
        }
        wake();
        notifyState();
    }

    /** Asks the active servers for an immediate probe (watchdog, network change). */
    void nudge() {
        for (Backend backend : backendIndex.values()) if (backend.active) requestProbe(backend);
        wake();
    }

    /** User pull-to-refresh: wake every reserve and check all route exits immediately. */
    void manualRefresh() {
        long now = System.currentTimeMillis();
        for (Group group : groupOrder) {
            for (Backend backend : group.candidates) {
                if (!backend.warm) {
                    backend.warm = true;
                    backend.warmedAt = now;
                    backend.signal();
                    log("BACKEND_WARMING name=" + safeName(backend.name()) + " route=" + group.label + " reason=manual");
                }
                requestProbe(backend);
            }
        }
        platform.keepAwake(15000);
        log("MANUAL_REFRESH all routes");
        wake();
    }

    void stop() {
        synchronized (sync) {
            if (disposed) return;
            requested = null;
            requestGeneration++;
            state = worker == null ? State.OFF : State.STOPPING;
            statusText = worker == null ? "VPN выключен" : "Останавливаю маршруты…";
        }
        wake();
        notifyState();
    }

    void dispose() {
        Thread current;
        synchronized (sync) {
            if (disposed) return;
            disposed = true;
            requested = null;
            requestGeneration++;
            current = worker;
        }
        wake();
        if (current != null && current != Thread.currentThread()) {
            try { current.join(8000); } catch (InterruptedException ignored) { }
        }
        probes.shutdownNow();
    }

    private void wake() { synchronized (wakeLock) { wakeFlag = true; wakeLock.notifyAll(); } }

    private boolean waitWake(long ms) {
        synchronized (wakeLock) {
            if (!wakeFlag && ms != 0) {
                try { if (ms < 0) wakeLock.wait(); else wakeLock.wait(ms); } catch (InterruptedException ignored) { }
            }
            boolean woken = wakeFlag;
            wakeFlag = false;
            return woken;
        }
    }

    private void workerLoop() {
        boolean blocked = false;
        try {
            while (!disposed) {
                Model.Config next; int generation;
                synchronized (sync) { next = requested; generation = requestGeneration; }
                waitWake(0);
                if (next == null) {
                    if (runtimeActive) tearDown();
                    adoptedGeneration = -1;
                    blocked = false;
                    setStopped();
                    if (disposed) break;
                    waitWake(-1);
                    continue;
                }
                if (generation != adoptedGeneration) {
                    adoptedGeneration = generation;
                    blocked = false;
                    try { apply(next, generation); }
                    catch (ConfigurationException error) {
                        tearDown();
                        setState(generation, State.ERROR, error.getMessage());
                        blocked = true;
                    } catch (Exception error) {
                        tearDown();
                        String detail = safeError(error);
                        log("APPLY_FAILED " + detail);
                        setState(generation, State.ERROR, detail + " Повтор через 5 с.");
                        adoptedGeneration = -1;
                        if (!disposed) waitWake(5000);
                        continue;
                    }
                }
                if (blocked) { waitWake(-1); continue; }
                try { supervise(generation); }
                catch (Exception error) { log("SUPERVISE_ERROR " + error.getClass().getSimpleName() + " " + safeError(error)); }
                if (!disposed) waitWake(250);
            }
        } finally {
            try { tearDown(); } catch (Exception ignored) { }
            setStopped();
        }
    }

    // ------------------------------------------------------------------ apply / reconcile

    private void apply(Model.Config request, int generation) throws Exception {
        config = request;
        if (!runtimeActive) {
            runtimeActive = true;
            dns = new DnsForwarder(this::dnsRelayPorts);
            routerCrashes.clear();
            nextRouterStart = 0;
            log("RUNTIME_STARTED");
        }
        reconcile(generation);
    }

    private List<Integer> dnsRelayPorts() {
        List<Integer> working = new ArrayList<>(), rest = new ArrayList<>();
        for (Group group : groupOrder) {
            Backend active = group.active;
            if (group.relay == null) continue;
            if (active != null && active.health() == Health.HEALTHY) working.add(group.relay.port);
            else rest.add(group.relay.port);
        }
        working.addAll(rest);
        return working;
    }

    private void reconcile(int generation) throws Exception {
        skipped.clear();
        skippedServers.clear();
        Set<String> claimed = new HashSet<>();
        List<Model.Route> activeRoutes = new ArrayList<>();
        Map<String, List<String>> routePackages = new HashMap<>();
        for (Model.Route route : config.routes) {
            if (!route.enabled) continue;
            List<String> packages = new ArrayList<>();
            for (String item : route.packages) {
                if (claimed.add(item)) packages.add(item);
                else skipped.add("«" + safeName(route.name) + "»: " + item + " уже в другом маршруте");
            }
            if (packages.isEmpty()) { if (!route.servers.isEmpty()) skipped.add("«" + safeName(route.name) + "»: не выбраны приложения"); continue; }
            String endpointError = SocksProbe.validateEndpoint(route.healthEndpoint);
            if (endpointError != null) { skipped.add("«" + safeName(route.name) + "»: " + endpointError); continue; }
            routePackages.put(route.id, packages);
            activeRoutes.add(route);
        }

        // Desired backends: every enabled server of every active route.
        Map<String, Object[]> desired = new LinkedHashMap<>();
        for (Model.Route route : activeRoutes) {
            int priority = 0;
            for (Model.Server server : route.activeServers()) {
                try {
                    Links.validate(server.link);
                    desired.put(server.id, new Object[] {route, server, fingerprint(server), priority++});
                } catch (Exception error) {
                    String reason = error instanceof IllegalArgumentException ? error.getMessage() : "некорректный ключ";
                    skipped.add("«" + safeName(server.name) + "»: " + reason);
                    skippedServers.put(server.id, reason);
                }
            }
        }
        for (Backend existing : new ArrayList<>(backends.values())) {
            Object[] match = desired.get(existing.id);
            if (match != null && match[2].equals(existing.fingerprint)) continue;
            stopBackend(existing, match == null ? "сервер выключен" : "настройки изменены");
        }
        for (Object[] item : desired.values()) {
            Model.Route route = (Model.Route) item[0];
            Model.Server server = (Model.Server) item[1];
            Backend backend = backends.get(server.id);
            int priority = (Integer) item[3];
            if (backend != null) {
                synchronized (backend.gate) { backend.server = server; backend.route = route; }
                backend.priority = priority;
            } else {
                backend = new Backend(route, server, (String) item[2]);
                backend.priority = priority;
                // Only the first servers of a route stay warm on a phone; further reserves start on demand.
                backend.warm = priority < WARM_SERVERS;
                if (backend.warm) backend.warmedAt = System.currentTimeMillis();
                backends.put(backend.id, backend);
                startBackend(backend);
            }
        }
        backendIndex = new HashMap<>(backends);

        // Groups: one per active route.
        Set<String> keep = new HashSet<>();
        for (Model.Route route : activeRoutes) keep.add(route.id);
        for (String stale : new ArrayList<>(groups.keySet())) {
            if (keep.contains(stale)) continue;
            Group group = groups.remove(stale);
            reservedPorts.remove(group.relay.port);
            group.relay.close();
            log("GROUP_REMOVED route=" + stale);
        }
        List<Group> order = new ArrayList<>();
        for (Model.Route route : activeRoutes) {
            Group group = groups.get(route.id);
            if (group == null) {
                group = new Group(route.id);
                group.relay = createRelay(group, 10810);
                groups.put(route.id, group);
                log("GROUP_CREATED route=" + safeName(route.name) + " port=" + group.relay.port);
            }
            group.label = safeName(route.name);
            group.telephony = route.telephony;
            if (!group.telephony) group.learnedSipTarget = "";
            group.packages = routePackages.get(route.id);
            List<Backend> candidates = new ArrayList<>();
            for (Model.Server server : route.activeServers()) {
                Backend backend = backends.get(server.id);
                if (backend != null) {
                    backend.learnedSipTarget = group.learnedSipTarget;
                    candidates.add(backend);
                }
            }
            group.candidates = candidates;
            if (group.active != null && !candidates.contains(group.active)) group.active = null;
            refreshTargets(group);
            order.add(group);
        }
        groupOrder = order;
        if (order.isEmpty())
            throw new ConfigurationException(skipped.isEmpty() ? "Нет включённых маршрутов с приложениями и серверами." : "Нет рабочих маршрутов. " + String.join("; ", skipped.subList(0, Math.min(3, skipped.size()))));
        updateActiveFlags();

        StringBuilder mapping = new StringBuilder();
        for (Group group : order) mapping.append(group.routeId).append('=').append(group.relay.port).append(':').append(String.join(",", group.packages)).append(';');
        if (!mapping.toString().equals(routerMapping) || !routerRunning) {
            routerMapping = mapping.toString();
            routerRunning = false;
            nextRouterStart = 0;
            ensureRouter(generation, false);
        }
        updateStatus(generation);
    }

    private RouteRelay createRelay(Group group, int preferred) throws IOException {
        IOException last = null;
        for (int attempt = 0; attempt < 60; attempt++) {
            int port = attempt < 50 ? preferred + attempt : 0;
            if (port != 0 && reservedPorts.contains(port)) continue;
            try {
                RouteRelay relay = new RouteRelay(group.routeId, port, group);
                reservedPorts.add(relay.port);
                return relay;
            } catch (IOException error) { last = error; }
        }
        throw new IOException("Не удалось открыть локальный вход маршрута: " + (last == null ? "нет свободного порта" : "порт занят"));
    }

    private static String fingerprint(Model.Server server) {
        try {
            byte[] digest = MessageDigest.getInstance("SHA-256").digest(server.link.trim().getBytes(java.nio.charset.StandardCharsets.UTF_8));
            return java.util.Base64.getEncoder().encodeToString(digest);
        } catch (Exception error) { return server.link; }
    }

    // ------------------------------------------------------------------ backend lifecycle

    private void startBackend(Backend backend) {
        Thread thread = new Thread(() -> backendLoop(backend), "trelay-backend");
        thread.setDaemon(true);
        thread.start();
        log("BACKEND_ADDED name=" + safeName(backend.name()));
    }

    private void stopBackend(Backend backend, String reason) {
        backends.remove(backend.id);
        for (Group group : groups.values()) {
            if (!group.candidates.contains(backend)) continue;
            List<Backend> list = new ArrayList<>(group.candidates);
            list.remove(backend);
            group.candidates = list;
            if (group.active == backend) group.active = null;
            refreshTargets(group);
            group.relay.evacuate(backend.target);
        }
        backend.cancelled = true;
        backend.signal();
        Runner runner;
        synchronized (backend.gate) { runner = backend.runner; backend.runner = null; backend.health = Health.DOWN; }
        stopRunner(runner);
        reservedPorts.remove(backend.target.port());
        log("BACKEND_STOPPED name=" + safeName(backend.name()) + " reason=" + reason);
    }

    private void stopRunner(Runner runner) {
        if (runner == null) return;
        try { runner.stop(); } catch (Exception error) { log("Runner cleanup: " + error.getClass().getSimpleName()); }
    }

    private void backendLoop(Backend backend) {
        int attempt = 0;
        while (!backend.cancelled) {
            if (!backend.warm) {
                setHealth(backend, Health.DOWN, "холодный резерв", true);
                while (!backend.cancelled && !backend.warm) acquire(backend, 60000);
                attempt = 0;
                continue;
            }
            Runner runner = null;
            long started = System.currentTimeMillis();
            try {
                setHealth(backend, Health.STARTING, attempt == 0 ? "запуск" : "перезапуск", false);
                Model.Server server;
                synchronized (backend.gate) { server = backend.server; }
                String key = server.link.trim();
                if (Links.isDynamic(key)) {
                    try { key = ShareLinkResolver.resolve(key); }
                    catch (ShareLinkResolver.ResolutionException error) { throw new IOException("«" + safeName(server.name) + "»: " + error.getMessage()); }
                }
                int port = allocatePort(backend.target.port());
                backend.target.port(port);
                synchronized (this) {
                    if (backend.cancelled || !runtimeActive) break;
                }
                runner = platform.startBackend(server.id, key, port);
                synchronized (backend.gate) { backend.runner = runner; }
                if (!waitForPort(port, runner, 10000, backend))
                    throw new IOException(runner.alive() ? "движок не открыл локальный порт за 10 с" : "движок завершился при запуске");
                log("BACKEND_READY name=" + safeName(server.name) + " port=" + port);
                healthLoop(backend, runner);
                if (backend.cancelled) break;
                if (!backend.warm) { log("BACKEND_COOLED name=" + safeName(server.name)); continue; }
                setHealth(backend, Health.DOWN, "движок остановился", true);
                log("BACKEND_EXITED name=" + safeName(server.name));
            } catch (IllegalArgumentException error) {
                setHealth(backend, Health.DOWN, error.getMessage(), true);
                log("BACKEND_CONFIG_ERROR name=" + safeName(backend.name()) + " kind=" + kindOf(backend) + " " + error.getMessage());
                // Only a changed configuration can fix this; reconcile replaces the backend.
                while (!backend.cancelled) acquire(backend, 60000);
                break;
            } catch (Throwable error) {
                if (backend.cancelled) break;
                String detail = safeError(error);
                setHealth(backend, Health.DOWN, detail, true);
                // The kind of key says which code path failed; it is not a secret and saves guessing.
                log("BACKEND_FAILED name=" + safeName(backend.name()) + " kind=" + kindOf(backend) + " " + detail);
            } finally {
                synchronized (backend.gate) { if (backend.runner == runner) backend.runner = null; }
                stopRunner(runner);
            }
            if (System.currentTimeMillis() - started > 60000) attempt = 0;
            long delay = attempt == 0 ? 500 : Math.min(30000, 1000L * (1 << Math.min(attempt - 1, 5)));
            attempt++;
            synchronized (backend.gate) { backend.detail = backend.detail + " · повтор через " + Math.max(1, delay / 1000) + " с"; }
            wake();
            long until = System.currentTimeMillis() + delay;
            while (!backend.cancelled && System.currentTimeMillis() < until) acquire(backend, until - System.currentTimeMillis());
        }
        synchronized (backend.gate) { backend.health = Health.DOWN; }
    }

    /** Which kind of key the failing backend holds; not a secret, and it saves guessing in the log. */
    private static String kindOf(Backend backend) {
        try {
            Model.Server server;
            synchronized (backend.gate) { server = backend.server; }
            return server == null ? "?" : Links.kindLabel(server.link);
        } catch (Throwable ignored) {
            return "?";
        }
    }

    private static void acquire(Backend backend, long ms) {
        try { backend.probeSignal.tryAcquire(Math.max(1, ms), TimeUnit.MILLISECONDS); } catch (InterruptedException ignored) { }
    }

    private void healthLoop(Backend backend, Runner runner) {
        int cycle = 0;
        while (!backend.cancelled && backend.warm && runner.alive()) {
            String[] failure = new String[1];
            boolean ok = probeBackend(backend, cycle++, failure, 0);
            if (backend.cancelled || !runner.alive()) return;
            // One more short end-to-end check through this very tunnel before treating a second timeout as an outage.
            if (!ok && backend.active && backend.health() == Health.HEALTHY) {
                int previous;
                synchronized (backend.gate) { previous = backend.failures; }
                if (previous == FAILURE_THRESHOLD - 1) {
                    String[] confirmation = new String[1];
                    if (probeBackend(backend, cycle++, confirmation, CONFIRM_PROBE_TIMEOUT_MS)) {
                        ok = true; failure[0] = "";
                        log("PROBE_CONFIRMED_ALIVE name=" + safeName(backend.name()));
                    } else if (!backend.cancelled) log("PROBE_CONFIRMED_DOWN name=" + safeName(backend.name()) + " detail=" + confirmation[0]);
                }
            }
            if (backend.cancelled || !runner.alive()) return;
            recordProbe(backend, ok, failure[0]);
            Health health; int failures;
            synchronized (backend.gate) { health = backend.health; failures = backend.failures; }
            long interval = failures > 0 && health == Health.HEALTHY ? CONFIRM_PROBE_DELAY_MS
                    : health == Health.STARTING ? STARTING_PROBE_INTERVAL_MS : probeInterval(backend);
            acquire(backend, interval);
        }
    }

    private long probeInterval(Backend backend) {
        boolean busy = false;
        for (Group group : groupOrder) if (group.candidates.contains(backend) && group.relay != null && group.relay.recentTraffic(BUSY_WINDOW)) busy = true;
        boolean interactive = platform.interactive();
        long interval;
        if (backend.active) interval = busy ? BUSY_PROBE_INTERVAL_MS : interactive ? ACTIVE_PROBE_INTERVAL_MS : IDLE_ACTIVE_PROBE_INTERVAL_MS;
        else if (busy || interactive) interval = STANDBY_PROBE_INTERVAL_MS;
        else interval = health(backend) == Health.UNHEALTHY ? STANDBY_PROBE_INTERVAL_MS : IDLE_STANDBY_PROBE_INTERVAL_MS;
        // A SIP check is an OPTIONS request to someone else's PBX. During a call the route is "busy"
        // all the time, and two requests a second look like an attack to an operator, so keep them rare.
        return Math.max(interval, sipProbeFloor(backend));
    }

    private long sipProbeFloor(Backend backend) {
        for (String target : probeTargets(backend)) {
            SocksProbe.HostPort parsed = SocksProbe.parseEndpoint(target);
            if (parsed != null && SocksProbe.isSipPort(parsed.port)) return SIP_PROBE_INTERVAL_MS;
        }
        return 0;
    }

    private static Health health(Backend backend) { return backend.health(); }

    private void recordProbe(Backend backend, boolean ok, String failure) {
        boolean changed = false;
        Health health;
        synchronized (backend.gate) {
            long now = System.currentTimeMillis();
            if (ok) {
                backend.failures = 0;
                backend.firstFailure = 0;
                backend.successes++;
                backend.lastSuccess = now;
                // A backend that failed before needs two clean probes; a fresh one is usable at once.
                if (backend.health == Health.STARTING || (backend.health == Health.UNHEALTHY && backend.successes >= 2)) {
                    backend.health = Health.HEALTHY; backend.healthySince = now; changed = true;
                }
                if (backend.health == Health.HEALTHY) backend.detail = "работает";
            } else {
                backend.successes = 0;
                if (backend.failures == 0) backend.firstFailure = now;
                backend.failures++;
                backend.lastFailure = now;
                backend.detail = failure == null || failure.isEmpty() ? "нет ответа" : failure;
                if ((backend.health == Health.HEALTHY && backend.failures >= FAILURE_THRESHOLD) ||
                        (backend.health == Health.STARTING && backend.failures >= STARTING_FAILURE_THRESHOLD)) {
                    backend.health = Health.UNHEALTHY; changed = true;
                }
            }
            health = backend.health;
        }
        if (!changed) return;
        log("HEALTH name=" + safeName(backend.name()) + " state=" + health + (ok ? "" : " " + failure));
        wake();
    }

    private void setHealth(Backend backend, Health health, String detail, boolean wakeEngine) {
        synchronized (backend.gate) {
            backend.health = health;
            backend.detail = detail;
            backend.failures = 0; backend.successes = 0; backend.firstFailure = 0;
            if (health == Health.DOWN || health == Health.UNHEALTHY) backend.lastFailure = System.currentTimeMillis();
        }
        if (wakeEngine) wake();
    }

    private void requestProbe(Backend backend) {
        long now = System.currentTimeMillis();
        if (now - backend.lastProbeRequest.get() < 1000) return;
        backend.lastProbeRequest.set(now);
        backend.signal();
    }

    private void onRelayFailure(RouteRelay.Target target, RouteRelay.Failure kind) {
        Backend backend = backendIndex.get(target.id);
        if (backend == null || backend.target != target) return;
        platform.keepAwake(10000);
        requestProbe(backend);
        if (kind == RouteRelay.Failure.LOCAL_CONNECT) wake();
    }

    private void onSipTarget(Group group, String host, int port, boolean udp) {
        if (!group.telephony || host == null || host.isEmpty() || !SocksProbe.isSipPort(port)) return;
        String shownHost = host.contains(":") ? "[" + host + "]" : host;
        String endpoint = (udp ? "udp://" : "tcp://") + shownHost + ":" + port;
        if (endpoint.equals(group.learnedSipTarget)) return;
        group.learnedSipTarget = endpoint;
        List<Backend> candidates = group.candidates;
        for (Backend backend : candidates) {
            backend.learnedSipTarget = endpoint;
            synchronized (backend.gate) {
                if (backend.health == Health.HEALTHY) {
                    backend.health = Health.STARTING;
                    backend.failures = 0;
                    backend.successes = 0;
                    backend.detail = "проверяю доступ к АТС";
                }
            }
            requestProbe(backend);
        }
        platform.keepAwake(15000);
        log("SIP_TARGET_LEARNED route=" + group.label + " target=" + endpoint);
        wake();
    }

    private List<String> probeTargets(Backend backend) {
        Model.Route route;
        synchronized (backend.gate) { route = backend.route; }
        List<String> targets = new ArrayList<>();
        String custom = route.healthEndpoint == null ? "" : route.healthEndpoint.trim();
        if (!custom.isEmpty()) targets.add(custom.contains(":") ? custom : custom + ":443");
        else if (route.telephony && backend.learnedSipTarget != null && !backend.learnedSipTarget.isEmpty())
            targets.add(backend.learnedSipTarget);
        if (targets.isEmpty()) Collections.addAll(targets, SocksProbe.INTERNET_TARGETS);
        return targets;
    }

    // One probe cycle: two targets in parallel, success as soon as either answers.
    private boolean probeBackend(Backend backend, int cycle, String[] failure, int timeoutOverride) {
        List<String> targets = probeTargets(backend);
        int port = backend.target.port();
        int timeout = timeoutOverride > 0 ? timeoutOverride : backend.active ? ACTIVE_PROBE_TIMEOUT_MS : STANDBY_PROBE_TIMEOUT_MS;
        // A "hold" check (no protocol to talk) must outlast the engine's dial retries, otherwise a dead
        // server still looks alive. SIP and HTTP checks get a real answer, so they stay fast.
        for (String target : targets) if (SocksProbe.isHoldOnly(target)) timeout = Math.max(timeout, 5000);
        failure[0] = "нет ответа";
        List<Future<SocksProbe.Result>> futures = new ArrayList<>();
        try {
            int attempts;
            synchronized (backend.gate) { attempts = backend.failures > 0 || backend.health != Health.HEALTHY || timeoutOverride > 0 ? 2 : 1; }
            for (int i = 0; i < attempts; i++) {
                String pick = targets.get((cycle + i) % targets.size());
                final int limit = timeout;
                futures.add(probes.submit(() -> SocksProbe.probe(port, pick, limit)));
            }
        } catch (Exception error) { return false; }
        long deadline = System.currentTimeMillis() + timeout + 1500;
        String first = null;
        List<Future<SocksProbe.Result>> pending = new ArrayList<>(futures);
        while (!pending.isEmpty() && System.currentTimeMillis() < deadline && !backend.cancelled) {
            for (Future<SocksProbe.Result> future : new ArrayList<>(pending)) {
                if (!future.isDone()) continue;
                pending.remove(future);
                try {
                    SocksProbe.Result result = future.get();
                    if (result.ok) { failure[0] = ""; for (Future<?> other : pending) other.cancel(true); return true; }
                    if (first == null) first = result.failure;
                } catch (Exception error) { if (first == null) first = error.getClass().getSimpleName(); }
            }
            if (!pending.isEmpty()) try { Thread.sleep(20); } catch (InterruptedException ignored) { }
        }
        for (Future<?> other : pending) other.cancel(true);
        failure[0] = first == null ? "нет ответа" : first;
        return false;
    }

    private int allocatePort(int previous) throws IOException {
        synchronized (reservedPorts) {
            if (previous > 0 && canBind(previous)) return previous;
            reservedPorts.remove(previous);
            for (int attempt = 0; attempt < 64; attempt++) {
                int port;
                try (ServerSocket socket = new ServerSocket()) {
                    socket.bind(new InetSocketAddress("127.0.0.1", 0));
                    port = socket.getLocalPort();
                }
                if (reservedPorts.contains(port) || !canBind(port)) continue;
                reservedPorts.add(port);
                return port;
            }
        }
        throw new IOException("Не нашёлся свободный внутренний порт.");
    }

    private static boolean canBind(int port) {
        if (port < 1025 || port > 65535) return false;
        try (ServerSocket tcp = new ServerSocket(); java.net.DatagramSocket udp = new java.net.DatagramSocket(null)) {
            tcp.setReuseAddress(false);
            tcp.bind(new InetSocketAddress("127.0.0.1", port));
            udp.setReuseAddress(false);
            udp.bind(new InetSocketAddress("127.0.0.1", port));
            return true;
        } catch (IOException error) { return false; }
    }

    private static boolean waitForPort(int port, Runner runner, long timeout, Backend backend) {
        long deadline = System.currentTimeMillis() + timeout;
        while (System.currentTimeMillis() < deadline) {
            if (backend.cancelled || !runner.alive()) return false;
            try (java.net.Socket socket = new java.net.Socket()) {
                socket.connect(new InetSocketAddress("127.0.0.1", port), 350);
                return runner.alive();
            } catch (IOException ignored) { }
            try { Thread.sleep(100); } catch (InterruptedException ignored) { }
        }
        return false;
    }

    // ------------------------------------------------------------------ supervision

    private void supervise(int generation) {
        ensureRouter(generation, true);
        for (Group group : groupOrder) select(group);
        updateActiveFlags();
        checkAppAlerts();
        updateStatus(generation);
    }

    private static boolean isAlive(Backend backend) {
        synchronized (backend.gate) { return backend.health != Health.DOWN && backend.runner != null && backend.runner.alive(); }
    }

    private void select(Group group) {
        long now = System.currentTimeMillis();
        List<Backend> candidates = group.candidates;
        Backend active = group.active;
        if (active != null && !candidates.contains(active)) active = group.active = null;
        long failureStarted = active == null ? 0 : firstFailure(active);
        Backend best = null;
        for (Backend candidate : candidates) {
            if (isFreshHealthy(candidate, now) && (active == null || active.health() != Health.UNHEALTHY || candidate == active || checkedSince(candidate, failureStarted))) {
                best = candidate;
                break;
            }
        }
        if (active != null && hasPendingFailure(active)) {
            platform.keepAwake(15000);
            for (Backend backup : candidates) if (backup != active && isAlive(backup)) requestProbe(backup);
        }
        manageWarmth(group, active, now);
        if (active == null) {
            if (best != null) activate(group, best, "initial");
            else if (now - group.createdAt > STARTUP_GRACE) {
                for (Backend candidate : candidates) if (isAlive(candidate)) { activate(group, candidate, "degraded"); break; }
            }
        } else {
            Health health = active.health();
            if (health == Health.DOWN || health == Health.UNHEALTHY || (health == Health.STARTING && !isAlive(active))) {
                if (best != null && best != active) failover(group, active, best);
                else if (health == Health.DOWN) {
                    for (Backend candidate : candidates) if (candidate != active && isAlive(candidate)) { failover(group, active, candidate); break; }
                }
            } else if (health == Health.HEALTHY && !hasPendingFailure(active) && best != null && best != active && best.priority < active.priority &&
                    now - group.lastFailover >= RECOVERY_HOLD && readyForRecovery(best, now)) {
                recover(group, active, best, neverFailed(best));
            }
        }
        refreshTargets(group);

        // After a recovery old connections may still run through a backup; move them if it dies.
        Backend current = group.active;
        if (current != null && current.health() == Health.HEALTHY) {
            for (Backend backend : candidates) {
                if (backend == current) continue;
                Health health = backend.health();
                if ((health == Health.DOWN || health == Health.UNHEALTHY) && group.relay.connectionCount(backend.target) > 0) {
                    int moved = group.relay.evacuate(backend.target);
                    log("EVACUATED name=" + safeName(backend.name()) + " connections=" + moved);
                }
            }
        }

        boolean working = current != null && current.health() == Health.HEALTHY;
        if (working) {
            group.lostSince = 0;
            if (group.lostAlerted) {
                group.lostAlerted = false;
                raise(AlertKind.ROUTE_RESTORED, false, "TRELAY: связь восстановлена", group.label + ": работает через «" + safeName(current.name()) + "».");
            }
        } else if (!group.lostAlerted && now - group.createdAt > STARTUP_GRACE && (current == null || current.health() != Health.STARTING) &&
                !(current == null && now - group.createdAt < 30000 && anyStarting(candidates))) {
            if (group.lostSince == 0) group.lostSince = now;
            if (now - group.lostSince >= ROUTE_LOSS_ALERT_HOLD) {
                group.lostAlerted = true;
                String reason = current == null ? "ни один сервер не запустился" : "«" + safeName(current.name()) + "»: " + detail(current);
                raise(AlertKind.ROUTE_LOST, true, "TRELAY: нет рабочего маршрута", group.label + ": " + reason + ". Приложения маршрута заблокированы, проверяю резервы.");
            }
        }
    }

    // Cold reserves: keep WARM_SERVERS running, start the next one while there are fewer healthy warm
    // servers than that (or the active one is in trouble), stop extra ones after five quiet minutes.
    private void manageWarmth(Group group, Backend active, long now) {
        List<Backend> candidates = group.candidates;
        int target = Math.min(WARM_SERVERS, candidates.size());
        int healthyWarm = 0;
        boolean starting = false;
        for (Backend candidate : candidates) {
            if (!candidate.warm) continue;
            Health health = candidate.health();
            if (health == Health.HEALTHY && candidate.failuresNow() == 0) healthyWarm++;
            if (health == Health.STARTING) starting = true;
        }
        boolean trouble = active == null ? now - group.createdAt > STARTUP_GRACE : hasPendingFailure(active) || active.health() != Health.HEALTHY;
        if ((trouble || healthyWarm < target) && !starting) {
            for (Backend candidate : candidates) {
                if (candidate.warm) continue;
                candidate.warm = true;
                candidate.warmedAt = now;
                candidate.signal();
                log("BACKEND_WARMING name=" + safeName(candidate.name()) + " route=" + group.label);
                break;
            }
            return;
        }
        if (trouble || healthyWarm <= target) return;
        for (Backend candidate : candidates) {
            if (!candidate.warm || candidate == active || candidate.priority < WARM_SERVERS) continue;
            if (now - candidate.warmedAt < COOL_DOWN_AFTER || group.relay.connectionCount(candidate.target) > 0) continue;
            candidate.warm = false;
            candidate.signal();
            break;
        }
    }

    private static boolean anyStarting(List<Backend> candidates) {
        for (Backend backend : candidates) if (backend.health() == Health.STARTING) return true;
        return false;
    }

    private static boolean hasPendingFailure(Backend backend) {
        synchronized (backend.gate) { return backend.health == Health.UNHEALTHY || (backend.health == Health.HEALTHY && backend.failures > 0); }
    }

    private static long firstFailure(Backend backend) { synchronized (backend.gate) { return backend.firstFailure; } }

    private static boolean checkedSince(Backend backend, long since) { synchronized (backend.gate) { return since == 0 || backend.lastSuccess >= since; } }

    private static boolean isFreshHealthy(Backend backend, long now) {
        synchronized (backend.gate) { return backend.health == Health.HEALTHY && backend.failures == 0 && now - backend.lastSuccess <= FRESH_BACKUP_WINDOW; }
    }

    private static boolean readyForRecovery(Backend backend, long now) {
        synchronized (backend.gate) {
            if (backend.health != Health.HEALTHY) return false;
            // A higher-priority server that never failed (it answered its first probe a moment later) takes new connections at once.
            if (backend.lastFailure == 0) return true;
            return backend.successes >= RECOVERY_SUCCESSES && now - backend.healthySince >= RECOVERY_HOLD && now - backend.lastFailure >= RECOVERY_HOLD;
        }
    }

    private static boolean neverFailed(Backend backend) { synchronized (backend.gate) { return backend.lastFailure == 0; } }

    private static String detail(Backend backend) { synchronized (backend.gate) { return backend.detail; } }

    private void activate(Group group, Backend backend, String reason) {
        group.active = backend;
        refreshTargets(group);
        backend.active = true;
        requestProbe(backend);
        log("ACTIVATED route=" + group.label + " name=" + safeName(backend.name()) + " reason=" + reason);
    }

    private void failover(Group group, Backend from, Backend to) {
        String reason = detail(from);
        int retry = reason.indexOf(" · повтор");
        if (retry > 0) reason = reason.substring(0, retry);
        group.active = to;
        refreshTargets(group);
        to.active = true;
        requestProbe(to);
        // Telephony: the PBX still sends incoming calls to the old server's address, so the client must
        // re-register through the new one immediately — drop every connection of the route, not just the
        // dead server's. Other routes are untouched.
        int moved = group.telephony ? group.relay.evacuateAll() : group.relay.evacuate(from.target);
        long now = System.currentTimeMillis();
        group.lastFailover = now;
        synchronized (routeFailoverAt) { routeFailoverAt.put(group.routeId, now); }
        log("FAILOVER route=" + group.label + " from=" + safeName(from.name()) + " to=" + safeName(to.name()) + " moved=" + moved + " reason=" + reason);
        String message = group.label + ": «" + safeName(from.name()) + "» — " + reason + ". Теперь через «" + safeName(to.name()) + "».";
        if (moved > 0) message += " Соединения приложения перезапущены — при необходимости перезайдите.";
        raise(AlertKind.FAILOVER, moved > 0, "TRELAY переключил сервер", message);
    }

    private void recover(Group group, Backend from, Backend to, boolean quiet) {
        group.active = to;
        refreshTargets(group);
        to.active = true;
        requestProbe(to);
        log("RECOVERY route=" + group.label + " from=" + safeName(from.name()) + " to=" + safeName(to.name()) + (quiet ? " (priority order at startup)" : ""));
        if (quiet) return;
        raise(AlertKind.RECOVERY, false, "TRELAY вернул основной сервер",
                group.label + ": новые соединения снова идут через «" + safeName(to.name()) + "». Текущие соединения не прерывались.");
    }

    // Order of backends a relay may use: selected one, then healthy, then any running one.
    private void refreshTargets(Group group) {
        Backend active = group.active;
        if (active == null) { group.targets = new RouteRelay.Target[0]; return; }
        List<RouteRelay.Target> list = new ArrayList<>();
        list.add(active.target);
        for (Backend backend : group.candidates) if (backend != active && backend.health() == Health.HEALTHY) list.add(backend.target);
        for (Backend backend : group.candidates) if (backend != active && backend.health() != Health.HEALTHY && isAlive(backend)) list.add(backend.target);
        group.targets = list.toArray(new RouteRelay.Target[0]);
    }

    private void updateActiveFlags() {
        Set<Backend> active = new HashSet<>();
        for (Group group : groups.values()) if (group.active != null) active.add(group.active);
        for (Backend backend : backends.values()) {
            boolean isActive = active.contains(backend);
            if (isActive == backend.active) continue;
            backend.active = isActive;
            if (isActive) requestProbe(backend);
        }
    }

    private void ensureRouter(int generation, boolean supervising) {
        if (routerRunning && platform.routerAlive()) return;
        long now = System.currentTimeMillis();
        if (routerRunning) {
            routerRunning = false;
            platform.stopRouter();
            routerCrashes.add(now);
            routerCrashes.removeIf(time -> now - time > 120000);
            long delay = routerCrashes.size() <= 1 ? 0 : Math.min(30, 2L * (1 << Math.min(routerCrashes.size() - 2, 4)));
            nextRouterStart = now + delay * 1000;
            log("ROUTER_EXITED restarts=" + routerCrashes.size() + " delay=" + delay);
            if (supervising && now - lastRouterAlert > 60000) {
                lastRouterAlert = now;
                raise(AlertKind.ROUTER_RESTARTED, true, "TRELAY: маршрутизатор перезапускается",
                        "Перехват приложений остановился — текущие соединения прерваны. Перезайдите в приложение после восстановления.");
            }
        }
        if (now < nextRouterStart || !runtimeActive) return;
        try {
            List<RouterGroup> mapping = new ArrayList<>();
            for (Group group : groupOrder) mapping.add(new RouterGroup(group.routeId, new ArrayList<>(group.packages), group.relay.port));
            platform.applyRouter(mapping, dns.port);
            routerRunning = true;
            routerStartedAt.set(System.currentTimeMillis());
            log("ROUTER_STARTED groups=" + mapping.size());
        } catch (Exception error) {
            nextRouterStart = System.currentTimeMillis() + 5000;
            log("ROUTER_START_FAILED " + safeError(error));
            setState(generation, State.ERROR, "Не удалось запустить VPN-маршрутизатор: " + safeError(error) + " Повтор через 5 с.");
        }
    }

    // ------------------------------------------------------------------ application signals (telephony)

    // Android cannot read another application's log like Windows does with Communicator. A route marked
    // as telephony instead watches its long-lived connections: when one is closed by the server or the
    // tunnel (not by the application) and no new connection to the same destination appears within
    // five seconds, the application has lost its signalling link.
    private void onLongConnectionClosed(Group group, RouteRelay.Closed info) {
        if (!group.telephony || "app".equals(info.closedBy)) return;
        long closedAt = System.currentTimeMillis();
        probes.execute(() -> {
            try { Thread.sleep(5000); } catch (InterruptedException ignored) { return; }
            if (disposed || group.relay == null) return;
            if (group.relay.hasLiveConnection(info.destination, closedAt)) { log("APP_RECONNECTED route=" + group.label + " dst=" + info.destination); return; }
            long now = System.currentTimeMillis();
            if (now - routerStartedAt.get() < APP_ALERT_QUIET_AFTER_ROUTER_START) return;
            synchronized (routeFailoverAt) {
                Long failover = routeFailoverAt.get(group.routeId);
                if (failover != null && now - failover < APP_ALERT_QUIET_AFTER_FAILOVER) return;
            }
            String key = group.routeId + "|" + info.destination;
            synchronized (appAlerted) {
                Long previous = appAlerted.get(key);
                if (previous != null && now - previous < 60000) return;
                appAlerted.put(key, now);
            }
            String side = "server".equals(info.closedBy) ? "сервер закрыл соединение" : "туннель прервал соединение";
            raise(AlertKind.APP_DISCONNECTED, true, group.label + ": обрыв связи",
                    group.label + " потерял долгое соединение с " + info.destination + " (" + side + " через " + (info.lifetimeMs / 60000) +
                            " мин) и не восстановил его. Перезайдите в приложение, не дожидаясь автоматического переподключения.");
        });
    }

    private void checkAppAlerts() {
        long now = System.currentTimeMillis();
        synchronized (appAlerted) { appAlerted.values().removeIf(time -> now - time > 600000); }
    }

    private void raise(AlertKind kind, boolean critical, String title, String message) {
        log("ALERT " + kind + (critical ? " critical " : " ") + message);
        Alert alert = new Alert(kind, critical, title, message);
        for (Listener listener : listeners) {
            try { listener.alert(alert); } catch (Exception error) { log("Alert subscriber failed: " + error.getClass().getSimpleName()); }
        }
    }

    // ------------------------------------------------------------------ status

    private void updateStatus(int generation) {
        Map<String, ServerInfo> info = new HashMap<>();
        for (Map.Entry<String, String> entry : skippedServers.entrySet()) info.put(entry.getKey(), new ServerInfo(ServerStatus.SKIPPED, entry.getValue()));
        for (Backend backend : backends.values()) {
            Health health; String text;
            synchronized (backend.gate) { health = backend.health; text = backend.detail; }
            ServerStatus status = !backend.warm && backend.runner == null ? ServerStatus.COLD : health == Health.STARTING ? ServerStatus.STARTING : health == Health.HEALTHY
                    ? (backend.active ? ServerStatus.ACTIVE : ServerStatus.STANDBY) : health == Health.UNHEALTHY ? ServerStatus.UNHEALTHY : ServerStatus.DOWN;
            info.put(backend.id, new ServerInfo(status, text));
        }
        Map<String, RouteInfo> routes = new HashMap<>();
        long now = System.currentTimeMillis();
        List<String> parts = new ArrayList<>();
        boolean anyStarting = false, anyLost = false;
        String routerProblem = routerRunning ? null : routerCrashes.size() >= 3
                ? "VPN-маршрутизатор завершается сразу после запуска" : "VPN-маршрутизатор перезапускается";
        for (Group group : groupOrder) {
            Backend active = group.active;
            int ready = 0, checking = 0, cold = 0;
            for (Backend backend : group.candidates) {
                if (backend == active) continue;
                if (!backend.warm) cold++;
                else if (backend.health() == Health.HEALTHY) ready++;
                else if (backend.health() == Health.STARTING) checking++;
            }
            String text;
            boolean working = false;
            if (active == null) {
                if (now - group.createdAt <= STARTUP_GRACE || anyStarting(group.candidates)) { anyStarting = true; text = "проверяю серверы — " + group.candidates.size(); }
                else { anyLost = true; text = "ни один сервер не отвечает, приложения заблокированы"; }
            } else {
                Health health = active.health();
                if (health == Health.HEALTHY) {
                    working = true;
                    String backups = ready > 0 ? "резерв готов: " + ready : checking > 0 ? "резервы проверяются" : cold > 0 ? "резерв запустится при сбое" : group.candidates.size() > 1 ? "резервы недоступны" : "без резерва";
                    if (ready > 0 && cold > 0) backups += ", холодных: " + cold;
                    text = "через «" + safeName(active.name()) + "», " + backups;
                    if (group.telephony && (active.route.healthEndpoint == null || active.route.healthEndpoint.trim().isEmpty())) {
                        if (group.learnedSipTarget == null || group.learnedSipTarget.isEmpty())
                            text += ", проверен интернет, жду первый SIP-запрос";
                        else
                            text += ", АТС проверена по " + (group.learnedSipTarget.startsWith("udp://") ? "SIP/UDP" : "SIP/TCP");
                    }
                } else if (health == Health.STARTING && now - group.createdAt <= 30000) {
                    anyStarting = true;
                    text = "«" + safeName(active.name()) + "»: проверяю";
                } else {
                    anyLost = true;
                    text = "«" + safeName(active.name()) + "» не отвечает (" + detail(active) + ")" + (ready == 0 ? ", рабочих резервов нет" : "");
                }
            }
            routes.put(group.routeId, new RouteInfo(working && routerProblem == null,
                    active == null ? null : active.id, routerProblem == null ? text : routerProblem));
            parts.add(group.label + ": " + text);
        }
        if (!skipped.isEmpty()) parts.add("пропущено: " + String.join("; ", skipped.subList(0, Math.min(2, skipped.size()))) + (skipped.size() > 2 ? " и ещё " + (skipped.size() - 2) : ""));
        if (routerProblem != null) { anyLost = true; parts.add(0, routerProblem); }
        State next = anyLost ? State.ERROR : anyStarting ? State.STARTING : State.READY;
        String text = parts.isEmpty() ? "Маршруты работают" : String.join(" · ", parts);
        boolean changed;
        synchronized (sync) {
            changed = !info.keySet().equals(serverInfo.keySet()) || statusSignature(info) != statusSignature(serverInfo) || !routes.toString().equals(routeInfo.toString());
            serverInfo = info;
            routeInfo = routes;
        }
        if (!setState(generation, next, text) && changed) notifyState();
    }

    private static int statusSignature(Map<String, ServerInfo> info) {
        int hash = 0;
        for (Map.Entry<String, ServerInfo> entry : info.entrySet()) hash += (entry.getKey() + entry.getValue().status + entry.getValue().detail).hashCode();
        return hash;
    }

    private void tearDown() {
        routerRunning = false;
        routerMapping = null;
        try { platform.stopRouter(); } catch (Exception ignored) { }
        for (Group group : groups.values()) { try { group.relay.close(); } catch (Exception ignored) { } }
        groups.clear();
        groupOrder = new ArrayList<>();
        List<Backend> stopping = new ArrayList<>(backends.values());
        synchronized (this) {
            runtimeActive = false;
            for (Backend backend : stopping) backend.cancelled = true;
        }
        for (Backend backend : stopping) {
            backend.signal();
            Runner runner;
            synchronized (backend.gate) { runner = backend.runner; backend.runner = null; }
            stopRunner(runner);
        }
        backends.clear();
        backendIndex = new HashMap<>();
        reservedPorts.clear();
        if (dns != null) { dns.close(); dns = null; }
        synchronized (appAlerted) { appAlerted.clear(); }
        synchronized (routeFailoverAt) { routeFailoverAt.clear(); }
        skipped.clear();
        skippedServers.clear();
        synchronized (sync) { serverInfo = new HashMap<>(); routeInfo = new HashMap<>(); }
    }

    private boolean setState(int generation, State next, String text) {
        synchronized (sync) {
            if (disposed || requested == null || requestGeneration != generation) return false;
            if (state == next && statusText.equals(text)) return false;
            state = next;
            statusText = text;
        }
        log(next + " " + text);
        notifyState();
        return true;
    }

    private void setStopped() {
        synchronized (sync) {
            if (!disposed && requested != null) return;
            if (state == State.OFF) return;
            state = State.OFF;
            statusText = "VPN выключен";
        }
        log("OFF VPN выключен");
        notifyState();
    }

    private void notifyState() {
        for (Listener listener : listeners) {
            try { listener.stateChanged(); } catch (Exception error) { log("State subscriber failed: " + error.getClass().getSimpleName()); }
        }
    }

    static String safeName(String name) {
        name = (name == null ? "Маршрут" : name).replace('\r', ' ').replace('\n', ' ').trim();
        return name.length() <= 80 ? name : name.substring(0, 80);
    }

    static String safeError(Throwable error) {
        if (error instanceof ConfigurationException || error instanceof IllegalArgumentException) return error.getMessage();
        if (error instanceof IOException && error.getMessage() != null && error.getMessage().matches(".*[а-яА-ЯёЁ].*")) return error.getMessage();
        if (error instanceof IOException) return "Ошибка сети или файлов движка.";
        String message = error.getMessage();
        if (message != null && message.matches(".*[а-яА-ЯёЁ].*") && message.length() < 300) return message;
        return "Ошибка движка (" + error.getClass().getSimpleName() + ").";
    }

    private void log(String line) { platform.log(line); }
}


