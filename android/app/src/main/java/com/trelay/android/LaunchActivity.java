package com.trelay.android;

import android.app.Activity;
import android.content.Intent;
import android.net.VpnService;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.widget.Button;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;

// Opens an application from a TRELAY shortcut only when its route works, so the app never
// starts without its VPN (for example a separate browser bound to one country).
public final class LaunchActivity extends Activity {
    static final String ACTION = "com.trelay.android.LAUNCH_APP";
    static final String EXTRA_PACKAGE = "package";
    private static final int VPN_REQUEST = 202;
    private static final long TIMEOUT_MS = 30000;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private String packageName;
    private Model.Route route;
    private TextView title, detail;
    private ProgressBar spinner;
    private LinearLayout actions;
    private long started;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        TrelayApp.init(this);
        packageName = getIntent().getStringExtra(EXTRA_PACKAGE);
        LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setGravity(Gravity.CENTER_HORIZONTAL);
        body.setBackground(Ui.shape(this, Ui.CARD, 18, Ui.STROKE));
        body.setPadding(Ui.dp(this, 24), Ui.dp(this, 22), Ui.dp(this, 24), Ui.dp(this, 20));
        ImageView icon = new ImageView(this);
        if (packageName != null) icon.setImageDrawable(AppCatalog.icon(this, packageName));
        body.addView(icon, new LinearLayout.LayoutParams(Ui.dp(this, 52), Ui.dp(this, 52)));
        title = Ui.text(this, "", 17, Ui.TEXT, true);
        title.setGravity(Gravity.CENTER);
        body.addView(title, Ui.fill(this, 12));
        detail = Ui.text(this, "", 13, Ui.MUTED, false);
        detail.setGravity(Gravity.CENTER);
        body.addView(detail, Ui.fill(this, 6));
        spinner = new ProgressBar(this);
        body.addView(spinner, Ui.wrap(this, 14));
        actions = Ui.row(this);
        actions.setGravity(Gravity.CENTER);
        body.addView(actions, Ui.fill(this, 14));
        Ui.fitSystemBars(body);
        setContentView(body);
        begin();
    }

    private void begin() {
        actions.removeAllViews();
        spinner.setVisibility(ProgressBar.VISIBLE);
        if (packageName == null || getPackageManager().getLaunchIntentForPackage(packageName) == null) { fail("Приложение не найдено", "Ярлык устарел: приложение удалено или не запускается."); return; }
        String label = AppCatalog.label(this, packageName);
        try { route = new ConfigStore(this).load().routeForPackage(packageName); }
        catch (Exception error) { fail("Хранилище недоступно", "Не удалось прочитать маршруты TRELAY."); return; }
        if (route == null) { fail(label + " без маршрута", "Приложение больше не входит во включённый маршрут TRELAY. Открой TRELAY и добавь его в маршрут."); return; }
        title.setText(label + " через «" + route.name + "»");
        detail.setText("Подключаю маршрут…");
        started = System.currentTimeMillis();
        Intent approval = VpnService.prepare(this);
        if (approval != null) { startActivityForResult(approval, VPN_REQUEST); return; }
        ensureRunning();
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != VPN_REQUEST) return;
        if (resultCode == RESULT_OK) ensureRunning();
        else fail("VPN не разрешён", "Без разрешения VPN приложение не будет открыто, чтобы трафик не ушёл мимо маршрута.");
    }

    private void ensureRunning() {
        TrelayVpnService.start(this); // also applies the current configuration when already running
        handler.postDelayed(this::poll, 400);
    }

    private void poll() {
        if (isFinishing()) return;
        TunnelEngine engine = TrelayVpnService.engine();
        TunnelEngine.RouteInfo info = engine == null ? null : engine.routeInfo().get(route.id);
        if (engine != null && engine.routerReady() && info != null && info.working) {
            Intent launch = getPackageManager().getLaunchIntentForPackage(packageName);
            if (launch != null) {
                launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                startActivity(launch);
            }
            finish();
            return;
        }
        if (info != null) detail.setText(info.text);
        else if (engine != null) detail.setText(engine.statusText());
        if (System.currentTimeMillis() - started > TIMEOUT_MS) {
            fail("Маршрут не работает", (info == null ? "" : info.text + "\n\n") + "Приложение не открыто, чтобы его трафик не ушёл мимо VPN.");
            return;
        }
        handler.postDelayed(this::poll, 300);
    }

    private void fail(String heading, String message) {
        spinner.setVisibility(ProgressBar.GONE);
        title.setText(heading);
        detail.setText(message);
        actions.removeAllViews();
        if (route != null) {
            Button retry = Ui.button(this, "Повторить", true);
            actions.addView(retry, Ui.sized(this, -2, 42, 0));
            retry.setOnClickListener(v -> begin());
        }
        Button open = Ui.button(this, "TRELAY", false);
        actions.addView(open, Ui.sized(this, -2, 42, 8));
        open.setOnClickListener(v -> { startActivity(new Intent(this, MainActivity.class)); finish(); });
        Button close = Ui.button(this, "Закрыть", false);
        actions.addView(close, Ui.sized(this, -2, 42, 8));
        close.setOnClickListener(v -> finish());
    }

    @Override protected void onDestroy() {
        handler.removeCallbacksAndMessages(null);
        super.onDestroy();
    }
}
