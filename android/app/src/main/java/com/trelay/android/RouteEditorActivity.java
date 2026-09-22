package com.trelay.android;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

public final class RouteEditorActivity extends Activity {
    static final String EXTRA_ROUTE = "route_id";
    static final String EXTRA_IMPORT = "import_text";

    private ConfigStore store;
    private Model.Config config;
    private Model.Route route;
    private boolean isNew;
    private EditText name, endpoint;
    private Switch telephony, enabled;
    private LinearLayout appsBox, serversBox;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        TrelayApp.init(this);
        getWindow().setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE);
        store = new ConfigStore(this);
        try { config = store.load(); }
        catch (Exception error) { Ui.error(this, "Хранилище недоступно", error); finish(); return; }
        String id = getIntent().getStringExtra(EXTRA_ROUTE);
        Model.Route existing = id == null ? null : config.route(id);
        if (existing != null) route = Model.Route.fromJsonSafe(existing);
        else { route = new Model.Route(); route.name = ""; isNew = true; }
        build();
        String shared = getIntent().getStringExtra(EXTRA_IMPORT);
        if (shared != null && !shared.trim().isEmpty()) importDialog(shared);
    }

    private void build() {
        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(Ui.BACKGROUND);
        LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(Ui.dp(this, 20), Ui.dp(this, 22), Ui.dp(this, 20), Ui.dp(this, 28));
        scroll.addView(body);

        body.addView(Ui.text(this, isNew ? "Новый маршрут" : "Маршрут", 24, Ui.TEXT, true));
        TextView intro = Ui.text(this, "Выбранные приложения пойдут только через серверы этого маршрута. Первый сервер — основной, остальные — прогретые резервы.", 13, Ui.MUTED, false);
        intro.setPadding(0, Ui.dp(this, 6), 0, Ui.dp(this, 10));
        body.addView(intro);

        LinearLayout main = Ui.card(this);
        main.addView(Ui.caption(this, "Название"));
        name = Ui.input(this, "Например: Россия / телефония", route.name, false);
        main.addView(name, Ui.fill(this, 8));
        enabled = Ui.toggle(this, "Маршрут включён", route.enabled);
        main.addView(enabled, Ui.fill(this, 10));
        telephony = Ui.toggle(this, "Телефония (SIP): быстрая перерегистрация при переключении, сигнал при обрыве и частые проверки во время разговора", route.telephony);
        main.addView(telephony, Ui.fill(this, 4));
        TextView telephonyHint = Ui.text(this, "Для Zoiper: транспорт TCP или TLS, «Keep alive» 30 с, «Register on network change» включить, фоновый режим Zoiper разрешить. При переключении сервера TRELAY сам обрывает соединения маршрута, чтобы Zoiper сразу зарегистрировался заново и принимал входящие.", 12, Ui.MUTED, false);
        telephonyHint.setPadding(0, Ui.dp(this, 6), 0, 0);
        main.addView(telephonyHint);
        body.addView(main, Ui.fill(this, 8));

        LinearLayout apps = Ui.card(this);
        LinearLayout appsHeader = Ui.row(this);
        appsHeader.addView(Ui.caption(this, "Приложения"), Ui.weight(this));
        Button pick = Ui.smallButton(this, "Выбрать");
        appsHeader.addView(pick, Ui.sized(this, -2, 36, 0));
        apps.addView(appsHeader);
        appsBox = new LinearLayout(this);
        appsBox.setOrientation(LinearLayout.VERTICAL);
        apps.addView(appsBox, Ui.fill(this, 8));
        pick.setOnClickListener(v -> AppCatalog.pick(this, new LinkedHashSet<>(route.packages), otherRoutePackages(), chosen -> {
            route.packages.clear();
            route.packages.addAll(chosen);
            refreshApps();
        }));
        body.addView(apps, Ui.fill(this, 12));

        LinearLayout servers = Ui.card(this);
        LinearLayout serversHeader = Ui.row(this);
        serversHeader.addView(Ui.caption(this, "Серверы · основной и резервы"), Ui.weight(this));
        Button add = Ui.smallButton(this, "+ Ключ / подписка");
        serversHeader.addView(add, Ui.sized(this, -2, 36, 0));
        servers.addView(serversHeader);
        serversBox = new LinearLayout(this);
        serversBox.setOrientation(LinearLayout.VERTICAL);
        servers.addView(serversBox, Ui.fill(this, 8));
        add.setOnClickListener(v -> importDialog(""));
        body.addView(servers, Ui.fill(this, 12));

        LinearLayout check = Ui.card(this);
        check.addView(Ui.caption(this, "Проверка сервиса через туннель"));
        TextView checkHint = Ui.text(this, "Для телефонии укажите host:port вашей АТС (например sip.example.com:5060). На SIP-портах проверяется ответ АТС по TCP. Если TLINE использует UDP, эта проверка не доказывает доступность регистрации по UDP. Без адреса проверяется только интернет.", 12, Ui.MUTED, false);
        checkHint.setPadding(0, Ui.dp(this, 6), 0, 0);
        check.addView(checkHint);
        endpoint = Ui.input(this, "host:port", route.healthEndpoint, false);
        check.addView(endpoint, Ui.fill(this, 8));
        body.addView(check, Ui.fill(this, 12));

        LinearLayout actions = Ui.row(this);
        Button save = Ui.button(this, "Сохранить", true);
        actions.addView(save, new LinearLayout.LayoutParams(0, Ui.dp(this, 46), 1));
        Button cancel = Ui.button(this, "Отмена", false);
        actions.addView(cancel, Ui.sized(this, 110, 46, 10));
        body.addView(actions, Ui.fill(this, 18));
        save.setOnClickListener(v -> save());
        cancel.setOnClickListener(v -> finish());
        if (!isNew) {
            Button delete = Ui.button(this, "Удалить маршрут", false);
            delete.setTextColor(Ui.BAD);
            body.addView(delete, new LinearLayout.LayoutParams(-1, Ui.dp(this, 44)));
            ((LinearLayout.LayoutParams) delete.getLayoutParams()).topMargin = Ui.dp(this, 10);
            delete.setOnClickListener(v -> new AlertDialog.Builder(this).setTitle("Удалить маршрут?").setMessage(route.name)
                    .setNegativeButton("Отмена", null).setPositiveButton("Удалить", (d, w) -> delete()).show());
        }
        Ui.fitSystemBars(scroll);
        setContentView(scroll);
        refreshApps();
        refreshServers();
    }

    private Set<String> otherRoutePackages() {
        Set<String> result = new HashSet<>();
        for (Model.Route other : config.routes) if (!other.id.equals(route.id) && other.enabled) result.addAll(other.packages);
        return result;
    }

    private void refreshApps() {
        appsBox.removeAllViews();
        if (route.packages.isEmpty()) {
            appsBox.addView(Ui.text(this, "Не выбрано. Например, приложение телефонии или отдельный браузер.", 13, Ui.MUTED, false));
            return;
        }
        for (String packageName : new ArrayList<>(route.packages)) {
            LinearLayout row = Ui.row(this);
            row.setPadding(0, Ui.dp(this, 4), 0, Ui.dp(this, 4));
            ImageView icon = new ImageView(this);
            icon.setImageDrawable(AppCatalog.icon(this, packageName));
            row.addView(icon, new LinearLayout.LayoutParams(Ui.dp(this, 30), Ui.dp(this, 30)));
            TextView label = Ui.ellipsized(Ui.text(this, AppCatalog.label(this, packageName), 14, Ui.TEXT, false));
            label.setPadding(Ui.dp(this, 10), 0, Ui.dp(this, 6), 0);
            row.addView(label, Ui.weight(this));
            Button remove = Ui.smallButton(this, "Убрать");
            row.addView(remove, Ui.sized(this, -2, 32, 0));
            remove.setOnClickListener(v -> { route.packages.remove(packageName); refreshApps(); });
            appsBox.addView(row);
        }
    }

    private void refreshServers() {
        serversBox.removeAllViews();
        if (route.servers.isEmpty()) {
            serversBox.addView(Ui.text(this, "Добавь VPN-ключ, KEY/SSCONF-ссылку, HTTPS-подписку или вставь Clash / sing-box / Xray конфиг.", 13, Ui.MUTED, false));
            return;
        }
        int reserve = 0;
        for (int i = 0; i < route.servers.size(); i++) {
            Model.Server server = route.servers.get(i);
            int index = i;
            LinearLayout item = new LinearLayout(this);
            item.setOrientation(LinearLayout.VERTICAL);
            item.setPadding(Ui.dp(this, 12), Ui.dp(this, 10), Ui.dp(this, 12), Ui.dp(this, 10));
            item.setBackground(Ui.shape(this, Ui.INPUT, 12, 0));
            LinearLayout top = Ui.row(this);
            String role = !server.enabled ? "ВЫКЛ" : reserve == 0 ? "ОСНОВНОЙ" : "РЕЗЕРВ " + reserve;
            if (server.enabled) reserve++;
            top.addView(Ui.badge(this, role, server.enabled ? Ui.SECONDARY : Ui.MUTED));
            TextView title = Ui.ellipsized(Ui.text(this, server.name, 15, Ui.TEXT, true));
            title.setPadding(Ui.dp(this, 10), 0, 0, 0);
            top.addView(title, Ui.weight(this));
            item.addView(top);
            TextView meta = Ui.ellipsized(Ui.text(this, Links.kindLabel(server.link) + " · " + Links.endpoint(server.link), 12, Ui.MUTED, false));
            meta.setPadding(0, Ui.dp(this, 5), 0, 0);
            item.addView(meta);
            LinearLayout buttons = Ui.row(this);
            Button up = Ui.smallButton(this, "↑");
            Button down = Ui.smallButton(this, "↓");
            Button toggle = Ui.smallButton(this, server.enabled ? "Выключить" : "Включить");
            Button rename = Ui.smallButton(this, "Имя");
            Button remove = Ui.smallButton(this, "Удалить");
            buttons.addView(up, Ui.sized(this, 40, 32, 0));
            buttons.addView(down, Ui.sized(this, 40, 32, 6));
            buttons.addView(toggle, Ui.sized(this, -2, 32, 6));
            buttons.addView(rename, Ui.sized(this, -2, 32, 6));
            buttons.addView(remove, Ui.sized(this, -2, 32, 6));
            item.addView(buttons, Ui.fill(this, 8));
            up.setEnabled(i > 0);
            down.setEnabled(i < route.servers.size() - 1);
            up.setOnClickListener(v -> { java.util.Collections.swap(route.servers, index, index - 1); refreshServers(); });
            down.setOnClickListener(v -> { java.util.Collections.swap(route.servers, index, index + 1); refreshServers(); });
            toggle.setOnClickListener(v -> { server.enabled = !server.enabled; refreshServers(); });
            remove.setOnClickListener(v -> { route.servers.remove(server); refreshServers(); });
            rename.setOnClickListener(v -> {
                EditText field = Ui.input(this, "Название сервера", server.name, false);
                LinearLayout wrap = new LinearLayout(this);
                wrap.setPadding(Ui.dp(this, 20), Ui.dp(this, 8), Ui.dp(this, 20), 0);
                wrap.addView(field, new LinearLayout.LayoutParams(-1, -2));
                new AlertDialog.Builder(this).setTitle("Название").setView(wrap).setNegativeButton("Отмена", null)
                        .setPositiveButton("Сохранить", (d, w) -> {
                            String value = Links.cleanName(field.getText().toString());
                            if (!value.isEmpty()) { server.name = value; refreshServers(); }
                        }).show();
            });
            serversBox.addView(item, Ui.fill(this, i == 0 ? 0 : 8));
        }
    }

    // ------------------------------------------------------------------ import

    private void importDialog(String initial) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(Ui.dp(this, 20), Ui.dp(this, 8), Ui.dp(this, 20), 0);
        EditText field = Ui.input(this, "vless://…, key://…, ssconf://…, https://подписка или текст конфига", initial, true);
        box.addView(field, new LinearLayout.LayoutParams(-1, -2));
        Button paste = Ui.smallButton(this, "Вставить из буфера");
        box.addView(paste, Ui.sized(this, -2, 34, 0));
        ((LinearLayout.LayoutParams) paste.getLayoutParams()).topMargin = Ui.dp(this, 8);
        paste.setOnClickListener(v -> {
            ClipboardManager clipboard = getSystemService(ClipboardManager.class);
            ClipData clip = clipboard.getPrimaryClip();
            if (clip != null && clip.getItemCount() > 0 && clip.getItemAt(0).coerceToText(this) != null)
                field.setText(clip.getItemAt(0).coerceToText(this).toString());
        });
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("Добавить серверы").setView(box)
                .setNegativeButton("Отмена", null).setPositiveButton("Добавить", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
            String text = field.getText().toString().trim();
            if (text.isEmpty()) return;
            dialog.dismiss();
            resolveImport(text, false);
        }));
        dialog.show();
        if (dialog.getWindow() != null) dialog.getWindow().setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE);
    }

    private void resolveImport(String text, boolean withIdentity) {
        String scheme = Links.scheme(text);
        boolean single = text.indexOf('\n') < 0 && !text.contains(" ");
        // Direct keys and KEY links are stored as they are; KEY is refreshed at every connection like on Windows.
        if (single && !scheme.equals("https") && !scheme.equals("ssconf") && Links.isSupportedLink(text)) {
            try {
                Links.validate(text);
                addServers(java.util.Collections.singletonList(text), text);
            } catch (IllegalArgumentException error) { Ui.error(this, "Ключ не добавлен", error); }
            return;
        }
        AlertDialog progress = Ui.progress(this, scheme.equals("https") ? "Загружаю подписку" : "Разбираю конфигурацию", "Получаю список серверов…");
        new Thread(() -> {
            try {
                String clientId = store.subscriptionClientId(withIdentity);
                List<String> links = ShareLinkResolver.resolveMany(text, clientId);
                runOnUiThread(() -> { progress.dismiss(); if (!isFinishing()) choose(links, text); });
            } catch (ShareLinkResolver.IdentityRequiredException identity) {
                runOnUiThread(() -> {
                    progress.dismiss();
                    if (isFinishing()) return;
                    new AlertDialog.Builder(this).setTitle("Подписке нужен ID телефона")
                            .setMessage("Сервис «" + identity.host + "» выдаёт серверы только с постоянным ID устройства (как Happ). TRELAY создаст случайный ID для этого телефона, сохранит его зашифрованным и отправит только этому сервису. Системные идентификаторы и серийные номера не передаются.")
                            .setNegativeButton("Отмена", null)
                            .setPositiveButton("Продолжить", (d, w) -> resolveImport(text, true)).show();
                });
            } catch (Exception error) {
                runOnUiThread(() -> { progress.dismiss(); Ui.error(this, "Серверы не получены", error); });
            }
        }, "trelay-import").start();
    }

    private void choose(List<String> links, String source) {
        List<String> usable = new ArrayList<>();
        for (String link : links) {
            try { Links.validate(link); usable.add(link); } catch (Exception ignored) { }
        }
        if (usable.isEmpty()) { Ui.error(this, "Нет подходящих серверов", new IllegalStateException("Ссылка не содержит серверов, которые умеет запускать TRELAY.")); return; }
        // A single-server SSCONF stays dynamic so the configuration refreshes on every connection.
        if (usable.size() == 1 && Links.scheme(source).equals("ssconf")) { addServers(java.util.Collections.singletonList(source.trim()), usable.get(0)); return; }
        if (usable.size() == 1) { addServers(usable, null); return; }
        String[] labels = new String[usable.size()];
        boolean[] selected = new boolean[usable.size()];
        int russian = 0;
        for (int i = 0; i < usable.size(); i++) {
            String link = usable.get(i);
            String label = Links.suggestName(link);
            labels[i] = (label.isEmpty() ? "Сервер " + (i + 1) : label) + "  ·  " + Links.kindLabel(link) + "  ·  " + Links.endpoint(link);
            if (russian < 3 && looksRussian(label)) { selected[i] = true; russian++; }
        }
        if (russian == 0) for (int i = 0; i < Math.min(3, selected.length); i++) selected[i] = true;
        new AlertDialog.Builder(this).setTitle("Выбери серверы (" + usable.size() + ")")
                .setMultiChoiceItems(labels, selected, (dialog, which, checked) -> selected[which] = checked)
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Добавить", (dialog, which) -> {
                    List<String> chosen = new ArrayList<>();
                    for (int i = 0; i < usable.size(); i++) if (selected[i]) chosen.add(usable.get(i));
                    if (chosen.isEmpty()) Ui.error(this, "Серверы не выбраны", new IllegalArgumentException("Отметь хотя бы один сервер."));
                    else addServers(chosen, null);
                }).show();
    }

    static boolean looksRussian(String label) {
        String probe = " " + label.toLowerCase(Locale.ROOT) + " ";
        return probe.contains("росси") || probe.contains("москв") || probe.contains("russia") || probe.contains("moscow") ||
                probe.contains("🇷🇺") || probe.matches(".*[^a-z]ru[^a-z].*") || probe.contains("спб") || probe.contains("петербург");
    }

    private void addServers(List<String> links, String nameSource) {
        Set<String> existing = new HashSet<>();
        for (Model.Server server : route.servers) existing.add(server.link.trim());
        int added = 0;
        for (String link : links) {
            if (!existing.add(link.trim())) continue;
            Model.Server server = new Model.Server();
            server.link = link.trim();
            String label = Links.suggestName(nameSource != null ? nameSource : link);
            if (label.isEmpty()) label = Links.kindLabel(link) + " " + (route.servers.size() + 1);
            server.name = label;
            route.servers.add(server);
            added++;
        }
        if (name.getText().toString().trim().isEmpty() && !route.servers.isEmpty()) name.setText(route.servers.get(0).name);
        refreshServers();
        if (added == 0) Ui.error(this, "Ничего не добавлено", new IllegalStateException("Эти серверы уже есть в маршруте."));
    }

    // ------------------------------------------------------------------ save

    private void save() {
        route.name = Links.cleanName(name.getText().toString());
        if (route.name.isEmpty()) route.name = route.servers.isEmpty() ? "Маршрут" : route.servers.get(0).name;
        route.enabled = enabled.isChecked();
        route.telephony = telephony.isChecked();
        route.healthEndpoint = endpoint.getText().toString().trim();
        String endpointError = SocksProbe.validateEndpoint(route.healthEndpoint);
        if (endpointError != null) { Ui.error(this, "Проверьте адрес", new IllegalArgumentException(endpointError)); return; }
        if (route.enabled && route.packages.isEmpty()) { Ui.error(this, "Нет приложений", new IllegalArgumentException("Выбери хотя бы одно приложение, которое пойдёт через этот маршрут.")); return; }
        if (route.enabled && route.activeServers().isEmpty()) { Ui.error(this, "Нет серверов", new IllegalArgumentException("Добавь хотя бы один включённый сервер.")); return; }
        Set<String> conflicts = new LinkedHashSet<>(route.packages);
        conflicts.retainAll(otherRoutePackages());
        if (route.enabled && !conflicts.isEmpty()) {
            List<String> labels = new ArrayList<>();
            for (String item : conflicts) labels.add(AppCatalog.label(this, item));
            new AlertDialog.Builder(this).setTitle("Приложения уже в другом маршруте")
                    .setMessage(String.join(", ", labels) + "\n\nОдно приложение может идти только через один маршрут. Перенести их сюда?")
                    .setNegativeButton("Отмена", null)
                    .setPositiveButton("Перенести", (d, w) -> persist(conflicts)).show();
            return;
        }
        persist(java.util.Collections.emptySet());
    }

    private void persist(Set<String> moveHere) {
        try {
            Model.Config fresh = store.load();
            for (Model.Route other : fresh.routes) if (!other.id.equals(route.id)) other.packages.removeAll(moveHere);
            boolean replaced = false;
            for (int i = 0; i < fresh.routes.size(); i++)
                if (fresh.routes.get(i).id.equals(route.id)) { fresh.routes.set(i, route); replaced = true; }
            if (!replaced) fresh.routes.add(route);
            store.save(fresh);
            TrelayVpnService.applyIfRunning(this);
            finish();
        } catch (Exception error) { Ui.error(this, "Не удалось сохранить", error); }
    }

    private void delete() {
        try {
            Model.Config fresh = store.load();
            fresh.routes.removeIf(item -> item.id.equals(route.id));
            store.save(fresh);
            TrelayVpnService.applyIfRunning(this);
            finish();
        } catch (Exception error) { Ui.error(this, "Не удалось удалить", error); }
    }
}
