package com.trelay.android;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageManager;
import android.content.pm.ShortcutInfo;
import android.content.pm.ShortcutManager;
import android.graphics.drawable.Icon;
import android.net.Uri;
import android.net.VpnService;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.Settings;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.widget.Button;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

public final class MainActivity extends Activity {
    private static final int VPN_REQUEST = 101;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private ConfigStore store;
    private Model.Config config = new Model.Config();
    private boolean storeAvailable = true;
    private ScrollView scroll;
    private int scrollY;
    private float pullStartY;
    private boolean pullEligible;
    private long lastManualRefresh;
    private String lastSignature = "";
    private final BroadcastReceiver updates = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) { render(false); }
    };
    private final Runnable ticker = new Runnable() {
        @Override public void run() { render(false); handler.postDelayed(this, 1000); }
    };

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        TrelayApp.init(this);
        TrelayVpnService.createChannels(this);
        getWindow().setStatusBarColor(Ui.BACKGROUND);
        getWindow().setNavigationBarColor(Ui.BACKGROUND);
        store = new ConfigStore(this);
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[] {Manifest.permission.POST_NOTIFICATIONS}, 7);
        handleShare(getIntent());
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleShare(intent);
    }

    @Override protected void onResume() {
        super.onResume();
        registerReceiver(updates, new IntentFilter(TrelayVpnService.STATE_CHANGED), Context.RECEIVER_NOT_EXPORTED);
        reload();
        render(true);
        handler.postDelayed(ticker, 1000);
    }

    @Override protected void onPause() {
        handler.removeCallbacks(ticker);
        unregisterReceiver(updates);
        super.onPause();
    }

    private void reload() {
        try { config = store.load(); storeAvailable = true; }
        catch (Exception error) {
            storeAvailable = false;
            new AlertDialog.Builder(this).setTitle("Не удалось открыть маршруты")
                    .setMessage("Зашифрованное хранилище недоступно. Данные не удалены.\n\n" + error.getClass().getSimpleName())
                    .setPositiveButton("Понятно", null).show();
        }
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == VPN_REQUEST && resultCode == RESULT_OK) TrelayVpnService.start(this);
    }

    private void connect() {
        if (!hasRunnableRoute()) {
            Ui.error(this, "Нечего подключать", new IllegalStateException("Добавь маршрут: выбери приложения и хотя бы один сервер."));
            return;
        }
        try {
            Intent approval = VpnService.prepare(this);
            if (approval == null) TrelayVpnService.start(this);
            else startActivityForResult(approval, VPN_REQUEST);
        } catch (Exception error) { Ui.error(this, "Не удалось запросить VPN", error); }
    }

    private boolean hasRunnableRoute() {
        for (Model.Route route : config.routes) if (route.enabled && !route.packages.isEmpty() && !route.activeServers().isEmpty()) return true;
        return false;
    }

    private void handleShare(Intent intent) {
        if (intent == null || !Intent.ACTION_SEND.equals(intent.getAction())) return;
        String shared = intent.getStringExtra(Intent.EXTRA_TEXT);
        intent.removeExtra(Intent.EXTRA_TEXT);
        if (shared == null || shared.trim().isEmpty()) return;
        reload();
        List<String> labels = new ArrayList<>();
        labels.add("+ Новый маршрут");
        for (Model.Route route : config.routes) labels.add(route.name);
        new AlertDialog.Builder(this).setTitle("Куда добавить серверы?")
                .setItems(labels.toArray(new String[0]), (dialog, which) -> {
                    Intent editor = new Intent(this, RouteEditorActivity.class).putExtra(RouteEditorActivity.EXTRA_IMPORT, shared);
                    if (which > 0) editor.putExtra(RouteEditorActivity.EXTRA_ROUTE, config.routes.get(which - 1).id);
                    startActivity(editor);
                }).setNegativeButton("Отмена", null).show();
    }

    // ------------------------------------------------------------------ rendering

    private String signature() {
        TunnelEngine engine = TrelayVpnService.engine();
        StringBuilder text = new StringBuilder(String.valueOf(storeAvailable));
        if (engine != null) {
            text.append(engine.state()).append(engine.statusText());
            for (Map.Entry<String, TunnelEngine.ServerInfo> entry : engine.serverInfo().entrySet())
                text.append(entry.getKey()).append(entry.getValue().status).append(entry.getValue().detail);
            text.append(engine.routeInfo());
        }
        try { text.append(config.toJson().toString().hashCode()); } catch (Exception ignored) { }
        return text.toString();
    }

    private void render(boolean force) {
        String signature = signature();
        if (!force && signature.equals(lastSignature)) return;
        lastSignature = signature;
        if (scroll != null) scrollY = scroll.getScrollY();
        TunnelEngine engine = TrelayVpnService.engine();
        TunnelEngine.State state = engine == null ? TunnelEngine.State.OFF : engine.state();
        Map<String, TunnelEngine.ServerInfo> servers = engine == null ? null : engine.serverInfo();
        Map<String, TunnelEngine.RouteInfo> routes = engine == null ? null : engine.routeInfo();

        scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(Ui.BACKGROUND);
        LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(Ui.dp(this, 16), Ui.dp(this, 14), Ui.dp(this, 16), Ui.dp(this, 20));
        scroll.addView(body);

        LinearLayout header = Ui.row(this);
        header.addView(Ui.text(this, "TRELAY", 22, Ui.TEXT, true), Ui.weight(this));
        TextView version = Ui.badge(this, "ANDROID  ·  " + TrelayApp.VERSION.toUpperCase(), Ui.SECONDARY);
        version.setPadding(Ui.dp(this, 8), Ui.dp(this, 5), Ui.dp(this, 8), Ui.dp(this, 5));
        header.addView(version);
        body.addView(header);
        TextView subtitle = Ui.text(this, "Отдельные маршруты для телефонии и выбранных приложений", 12, Ui.MUTED, false);
        subtitle.setPadding(0, Ui.dp(this, 2), 0, Ui.dp(this, 10));
        body.addView(subtitle);

        LinearLayout status = Ui.card(this);
        status.setPadding(Ui.dp(this, 16), Ui.dp(this, 14), Ui.dp(this, 16), Ui.dp(this, 14));
        status.addView(Ui.caption(this, "Состояние"));
        LinearLayout stateRow = Ui.row(this);
        TextView dot = Ui.text(this, "●", 18, stateColor(state), true);
        stateRow.addView(dot);
        TextView stateText = Ui.text(this, state == TunnelEngine.State.OFF ? "VPN выключен" : TrelayVpnService.stateTitle(state), 18, Ui.TEXT, true);
        stateText.setPadding(Ui.dp(this, 8), 0, 0, 0);
        stateRow.addView(stateText);
        status.addView(stateRow, Ui.fill(this, 6));
        String detail = engine == null ? "В VPN попадают только приложения из маршрутов. Остальной трафик телефона идёт как обычно." : engine.statusText();
        TextView explanation = Ui.text(this, detail, 13, Ui.MUTED, false);
        explanation.setPadding(0, Ui.dp(this, 6), 0, 0);
        status.addView(explanation);
        boolean on = engine != null && state != TunnelEngine.State.OFF;
        Button toggle = Ui.button(this, on ? "Отключить" : "Подключить", !on);
        status.addView(toggle, new LinearLayout.LayoutParams(-1, Ui.dp(this, 42)));
        ((LinearLayout.LayoutParams) toggle.getLayoutParams()).topMargin = Ui.dp(this, 10);
        toggle.setEnabled(storeAvailable);
        toggle.setOnClickListener(v -> { if (on) TrelayVpnService.stop(this); else connect(); handler.postDelayed(() -> render(true), 300); });
        body.addView(status);

        addBackgroundHint(body);

        LinearLayout routeHeader = Ui.row(this);
        routeHeader.addView(Ui.text(this, "Маршруты", 17, Ui.TEXT, true), Ui.weight(this));
        Button add = Ui.button(this, "+  Маршрут", true);
        routeHeader.addView(add, Ui.sized(this, -2, 38, 0));
        add.setEnabled(storeAvailable);
        add.setOnClickListener(v -> startActivity(new Intent(this, RouteEditorActivity.class)));
        LinearLayout.LayoutParams routeHeaderParams = Ui.fill(this, 14);
        routeHeaderParams.bottomMargin = Ui.dp(this, 8);
        body.addView(routeHeader, routeHeaderParams);

        if (config.routes.isEmpty()) {
            LinearLayout empty = Ui.card(this);
            empty.addView(Ui.text(this, "Маршрутов пока нет", 16, Ui.TEXT, true));
            TextView hint = Ui.text(this, "Создай маршрут: выбери приложение телефонии или браузер, добавь VPN-ключ или подписку. Можно прислать ключ сюда через «Поделиться».", 13, Ui.MUTED, false);
            hint.setPadding(0, Ui.dp(this, 7), 0, 0);
            empty.addView(hint);
            body.addView(empty);
        }
        for (int i = 0; i < config.routes.size(); i++) body.addView(routeCard(config.routes.get(i), i, servers, routes), Ui.fill(this, i == 0 ? 0 : 10));

        LinearLayout footer = Ui.row(this);
        Button settings = Ui.smallButton(this, "Настройки и сигналы");
        footer.addView(settings, Ui.sized(this, -2, 38, 0));
        settings.setOnClickListener(v -> startActivity(new Intent(this, SettingsActivity.class)));
        body.addView(footer, Ui.fill(this, 20));
        TextView note = Ui.text(this, "Ключи защищены Android Keystore · " + XrayCore.version(), 11, Ui.MUTED, false);
        note.setPadding(Ui.dp(this, 2), Ui.dp(this, 12), 0, 0);
        body.addView(note);

        Ui.fitSystemBars(scroll);
        installPullToRefresh(scroll);
        setContentView(scroll);
        scroll.post(() -> scroll.scrollTo(0, scrollY));
    }

    private void installPullToRefresh(ScrollView view) {
        view.setOnTouchListener((ignored, event) -> {
            if (event.getActionMasked() == MotionEvent.ACTION_DOWN) {
                pullStartY = event.getY();
                pullEligible = view.getScrollY() == 0;
            } else if (event.getActionMasked() == MotionEvent.ACTION_MOVE && view.getScrollY() > 0) {
                pullEligible = false;
            } else if (event.getActionMasked() == MotionEvent.ACTION_UP) {
                float distance = event.getY() - pullStartY;
                long now = System.currentTimeMillis();
                if (pullEligible && view.getScrollY() == 0 && distance >= Ui.dp(this, 72) && now - lastManualRefresh > 1500) {
                    lastManualRefresh = now;
                    boolean started = TrelayVpnService.manualRefresh(this);
                    Toast.makeText(this, started
                            ? "Проверяю все серверы и перерегистрирую TLINE"
                            : "Сначала подключи TRELAY", Toast.LENGTH_SHORT).show();
                    if (started) handler.postDelayed(() -> render(true), 300);
                }
                pullEligible = false;
            } else if (event.getActionMasked() == MotionEvent.ACTION_CANCEL) {
                pullEligible = false;
            }
            return false;
        });
    }

    private void addBackgroundHint(LinearLayout body) {
        boolean telephony = false;
        for (Model.Route route : config.routes) if (route.enabled && route.telephony) telephony = true;
        PowerManager power = getSystemService(PowerManager.class);
        if (!telephony || power.isIgnoringBatteryOptimizations(getPackageName())) return;
        LinearLayout card = Ui.card(this);
        card.addView(Ui.text(this, "Разреши работу в фоне", 15, Ui.WARN, true));
        TextView text = Ui.text(this, "Для телефонии Android не должен усыплять TRELAY: иначе проверки и переключение резервов замедлятся при выключенном экране.", 13, Ui.MUTED, false);
        text.setPadding(0, Ui.dp(this, 6), 0, 0);
        card.addView(text);
        Button allow = Ui.smallButton(this, "Разрешить");
        card.addView(allow, Ui.wrap(this, 10));
        allow.getLayoutParams().height = Ui.dp(this, 36);
        allow.setOnClickListener(v -> {
            try { startActivity(new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:" + getPackageName()))); }
            catch (Exception error) { startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)); }
        });
        body.addView(card, Ui.fill(this, 12));
    }

    private static int stateColor(TunnelEngine.State state) {
        switch (state) {
            case READY: return Ui.GOOD;
            case STARTING: case STOPPING: return Ui.WARN;
            case ERROR: return Ui.BAD;
            default: return Ui.MUTED;
        }
    }

    private View routeCard(Model.Route route, int index, Map<String, TunnelEngine.ServerInfo> serverInfo, Map<String, TunnelEngine.RouteInfo> routeInfo) {
        LinearLayout card = Ui.card(this);
        LinearLayout top = Ui.row(this);
        TextView title = Ui.ellipsized(Ui.text(this, route.name, 17, route.enabled ? Ui.TEXT : Ui.MUTED, true));
        top.addView(title, Ui.weight(this));
        if (route.telephony) top.addView(Ui.badge(this, "ТЕЛЕФОНИЯ", Ui.SECONDARY));
        if (!route.enabled) { TextView off = Ui.badge(this, "ВЫКЛ", Ui.MUTED); top.addView(off); ((LinearLayout.LayoutParams) off.getLayoutParams()).leftMargin = Ui.dp(this, 6); }
        card.addView(top);

        TunnelEngine.RouteInfo info = routeInfo == null ? null : routeInfo.get(route.id);
        String summary = info != null ? info.text : route.packages.size() + " прил. · " + route.activeServers().size() + " серв.";
        TextView line = Ui.text(this, summary, 12, info == null ? Ui.MUTED : info.working ? Ui.GOOD : Ui.WARN, false);
        line.setPadding(0, Ui.dp(this, 4), 0, 0);
        card.addView(line);

        LinearLayout apps = Ui.row(this);
        apps.setPadding(0, Ui.dp(this, 10), 0, 0);
        int shown = 0;
        for (String packageName : route.packages) {
            if (shown == 5) break;
            ImageView icon = new ImageView(this);
            icon.setImageDrawable(AppCatalog.icon(this, packageName));
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(Ui.dp(this, 26), Ui.dp(this, 26));
            params.rightMargin = Ui.dp(this, 6);
            apps.addView(icon, params);
            shown++;
        }
        List<String> names = new ArrayList<>();
        for (String packageName : route.packages) names.add(AppCatalog.label(this, packageName));
        TextView appNames = Ui.ellipsized(Ui.text(this, names.isEmpty() ? "приложения не выбраны" : String.join(", ", names), 12, Ui.MUTED, false));
        apps.addView(appNames, Ui.weight(this));
        card.addView(apps);

        int reserve = 0;
        for (Model.Server server : route.servers) {
            LinearLayout row = Ui.row(this);
            row.setPadding(0, Ui.dp(this, 8), 0, 0);
            TunnelEngine.ServerInfo status = serverInfo == null ? null : serverInfo.get(server.id);
            TextView dot = Ui.text(this, "●", 12, status == null ? (server.enabled ? Ui.STROKE : Ui.INPUT) : Ui.statusColor(status.status), true);
            row.addView(dot);
            String role = !server.enabled ? "выкл" : reserve == 0 ? "основной" : "резерв " + reserve;
            if (server.enabled) reserve++;
            TextView label = Ui.ellipsized(Ui.text(this, server.name + "  ·  " + role, 13, server.enabled ? Ui.TEXT : Ui.MUTED, false));
            label.setPadding(Ui.dp(this, 8), 0, Ui.dp(this, 8), 0);
            row.addView(label, Ui.weight(this));
            TextView kind = Ui.text(this, status == null ? Links.kindLabel(server.link) : Ui.statusLabel(status.status), 11, status == null ? Ui.MUTED : Ui.statusColor(status.status), false);
            row.addView(kind);
            card.addView(row);
            if (status != null && (status.status == TunnelEngine.ServerStatus.UNHEALTHY || status.status == TunnelEngine.ServerStatus.DOWN || status.status == TunnelEngine.ServerStatus.SKIPPED)) {
                TextView why = Ui.text(this, status.detail, 11, Ui.MUTED, false);
                why.setPadding(Ui.dp(this, 20), Ui.dp(this, 2), 0, 0);
                card.addView(why);
            }
        }

        LinearLayout buttons = Ui.row(this);
        Button edit = Ui.smallButton(this, "Изменить");
        Button up = Ui.smallButton(this, "↑");
        Button down = Ui.smallButton(this, "↓");
        Button power = Ui.smallButton(this, route.enabled ? "Выключить" : "Включить");
        Button shortcut = Ui.smallButton(this, "Ярлык");
        // The three labels share whatever width the screen has; the arrows keep a fixed narrow slot.
        // Fixed widths used to push the last button off the edge on a narrow phone.
        buttons.addView(edit, Ui.share(this, 34, 0));
        buttons.addView(power, Ui.share(this, 34, 6));
        buttons.addView(shortcut, Ui.share(this, 34, 6));
        buttons.addView(up, Ui.sized(this, 34, 34, 6));
        buttons.addView(down, Ui.sized(this, 34, 34, 6));
        up.setEnabled(index > 0);
        down.setEnabled(index < config.routes.size() - 1);
        card.addView(buttons, Ui.fill(this, 12));
        edit.setOnClickListener(v -> startActivity(new Intent(this, RouteEditorActivity.class).putExtra(RouteEditorActivity.EXTRA_ROUTE, route.id)));
        up.setOnClickListener(v -> mutate(c -> java.util.Collections.swap(c.routes, index, index - 1)));
        down.setOnClickListener(v -> mutate(c -> java.util.Collections.swap(c.routes, index, index + 1)));
        power.setOnClickListener(v -> mutate(c -> { Model.Route item = c.route(route.id); if (item != null) item.enabled = !item.enabled; }));
        shortcut.setOnClickListener(v -> shortcutDialog(route));
        return card;
    }

    private interface Mutation { void apply(Model.Config config); }

    private void mutate(Mutation mutation) {
        try {
            Model.Config fresh = store.load();
            mutation.apply(fresh);
            store.save(fresh);
            config = fresh;
            TrelayVpnService.applyIfRunning(this);
            render(true);
        } catch (Exception error) { Ui.error(this, "Не удалось сохранить", error); }
    }

    // A home-screen icon that opens the application only after its route is really working.
    private void shortcutDialog(Model.Route route) {
        if (route.packages.isEmpty()) { Ui.error(this, "Нет приложений", new IllegalStateException("Сначала выбери приложения маршрута.")); return; }
        String[] labels = new String[route.packages.size()];
        for (int i = 0; i < labels.length; i++) labels[i] = AppCatalog.label(this, route.packages.get(i));
        new AlertDialog.Builder(this).setTitle("Ярлык «открыть через " + route.name + "»")
                .setItems(labels, (dialog, which) -> pinShortcut(route, route.packages.get(which), labels[which]))
                .setNegativeButton("Отмена", null).show();
    }

    private void pinShortcut(Model.Route route, String packageName, String label) {
        ShortcutManager shortcuts = getSystemService(ShortcutManager.class);
        if (shortcuts == null || !shortcuts.isRequestPinShortcutSupported()) {
            Ui.error(this, "Ярлыки недоступны", new IllegalStateException("Лаунчер телефона не поддерживает закреплённые ярлыки."));
            return;
        }
        Intent launch = new Intent(this, LaunchActivity.class).setAction(LaunchActivity.ACTION)
                .putExtra(LaunchActivity.EXTRA_PACKAGE, packageName).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
        String shortLabel = label.length() > 10 ? label.substring(0, 10) : label;
        ShortcutInfo info = new ShortcutInfo.Builder(this, "launch-" + packageName)
                .setShortLabel(shortLabel + " · VPN").setLongLabel(label + " через " + route.name)
                .setIcon(Icon.createWithBitmap(AppCatalog.shortcutIcon(this, packageName))).setIntent(launch).build();
        try { shortcuts.requestPinShortcut(info, null); }
        catch (Exception error) { Ui.error(this, "Ярлык не создан", error); }
    }
}

