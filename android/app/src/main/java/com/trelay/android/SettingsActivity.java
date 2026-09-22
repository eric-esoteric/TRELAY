package com.trelay.android;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Bundle;
import android.provider.Settings;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.Switch;
import android.widget.TextView;
import java.util.List;

public final class SettingsActivity extends Activity {
    private ConfigStore store;
    private Model.Config config;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        TrelayApp.init(this);
        store = new ConfigStore(this);
        try { config = store.load(); }
        catch (Exception error) { Ui.error(this, "Хранилище недоступно", error); finish(); return; }
        build();
    }

    private void build() {
        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(Ui.BACKGROUND);
        LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(Ui.dp(this, 20), Ui.dp(this, 22), Ui.dp(this, 20), Ui.dp(this, 28));
        scroll.addView(body);
        body.addView(Ui.text(this, "Настройки", 24, Ui.TEXT, true));

        LinearLayout alerts = Ui.card(this);
        alerts.addView(Ui.caption(this, "Оповещения"));
        TextView alertsHint = Ui.text(this, "Громкий сигнал звучит только при подтверждённой серьёзной проблеме: переключение с обрывом соединений, потеря всех серверов маршрута, обрыв телефонии, отключение VPN. Восстановление — только уведомлением.", 12, Ui.MUTED, false);
        alertsHint.setPadding(0, Ui.dp(this, 6), 0, Ui.dp(this, 4));
        alerts.addView(alertsHint);
        Switch sound = Ui.toggle(this, "Звуковой сигнал", config.settings.soundAlerts);
        alerts.addView(sound, Ui.fill(this, 4));
        TextView outputLabel = Ui.text(this, "Аудиовыход сигнала", 13, Ui.MUTED, false);
        alerts.addView(outputLabel, Ui.fill(this, 8));
        Spinner output = new Spinner(this);
        List<AlertSound.Output> outputs = AlertSound.outputs(this);
        ArrayAdapter<AlertSound.Output> adapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, outputs);
        output.setAdapter(adapter);
        int selected = 0;
        for (int i = 0; i < outputs.size(); i++) if (outputs.get(i).id.equals(config.settings.alertOutput)) selected = i;
        output.setSelection(selected);
        output.setBackground(Ui.shape(this, Ui.INPUT, 10, 0));
        alerts.addView(output, Ui.fill(this, 6));
        Switch loud = Ui.toggle(this, "Поднимать громкость будильника до максимума на время сигнала", config.settings.maxAlarmVolume);
        alerts.addView(loud, Ui.fill(this, 8));
        Switch vibrate = Ui.toggle(this, "Вибрация", config.settings.vibrate);
        alerts.addView(vibrate, Ui.fill(this, 2));
        Button test = Ui.smallButton(this, "Проверить звук");
        alerts.addView(test, Ui.sized(this, -2, 38, 0));
        ((LinearLayout.LayoutParams) test.getLayoutParams()).topMargin = Ui.dp(this, 10);
        test.setOnClickListener(v -> {
            Model.Settings probe = collect(sound, output, loud, vibrate);
            AlertSound.play(this, true, probe, true);
        });
        body.addView(alerts, Ui.fill(this, 14));

        LinearLayout startup = Ui.card(this);
        startup.addView(Ui.caption(this, "Запуск и фон"));
        Switch autostart = Ui.toggle(this, "Включать VPN после перезагрузки телефона", config.settings.autostart);
        startup.addView(autostart, Ui.fill(this, 6));
        TextView alwaysHint = Ui.text(this, "Надёжнее всего включить «Постоянный VPN» для TRELAY в системных настройках. Не включай «Блокировать соединения без VPN»: в VPN входят только выбранные приложения, остальные тогда потеряют сеть. Строгий «Частный DNS» (конкретный хост) тоже отключи — DNS приложений маршрутов TRELAY отправляет через туннель сам.", 12, Ui.MUTED, false);
        alwaysHint.setPadding(0, Ui.dp(this, 6), 0, 0);
        startup.addView(alwaysHint);
        LinearLayout systemButtons = Ui.row(this);
        Button vpn = Ui.smallButton(this, "Настройки VPN");
        Button battery = Ui.smallButton(this, "Работа в фоне");
        systemButtons.addView(vpn, Ui.sized(this, -2, 36, 0));
        systemButtons.addView(battery, Ui.sized(this, -2, 36, 8));
        startup.addView(systemButtons, Ui.fill(this, 10));
        vpn.setOnClickListener(v -> startActivity(new Intent(Settings.ACTION_VPN_SETTINGS)));
        battery.setOnClickListener(v -> {
            try { startActivity(new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:" + getPackageName()))); }
            catch (Exception error) { startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)); }
        });
        body.addView(startup, Ui.fill(this, 12));

        LinearLayout log = Ui.card(this);
        log.addView(Ui.caption(this, "Журнал движка"));
        TextView logHint = Ui.text(this, "Переключения, проверки, закрытые долгие соединения (кто закрыл, сколько жило). Ключи и пароли в журнал не пишутся.", 12, Ui.MUTED, false);
        logHint.setPadding(0, Ui.dp(this, 6), 0, 0);
        log.addView(logHint);
        LinearLayout logButtons = Ui.row(this);
        Button show = Ui.smallButton(this, "Показать");
        Button share = Ui.smallButton(this, "Поделиться");
        Button clear = Ui.smallButton(this, "Очистить");
        logButtons.addView(show, Ui.sized(this, -2, 36, 0));
        logButtons.addView(share, Ui.sized(this, -2, 36, 8));
        logButtons.addView(clear, Ui.sized(this, -2, 36, 8));
        log.addView(logButtons, Ui.fill(this, 10));
        show.setOnClickListener(v -> {
            TextView text = Ui.text(this, EngineLog.tail(this, 60000), 11, Ui.TEXT, false);
            text.setTypeface(Typeface.MONOSPACE);
            text.setTextIsSelectable(true);
            text.setPadding(Ui.dp(this, 16), Ui.dp(this, 8), Ui.dp(this, 16), Ui.dp(this, 8));
            ScrollView box = new ScrollView(this);
            box.addView(text);
            AlertDialog dialog = new AlertDialog.Builder(this).setTitle("engine.log").setView(box).setPositiveButton("Закрыть", null).show();
            box.post(() -> box.fullScroll(ScrollView.FOCUS_DOWN));
        });
        share.setOnClickListener(v -> startActivity(Intent.createChooser(new Intent(Intent.ACTION_SEND).setType("text/plain")
                .putExtra(Intent.EXTRA_SUBJECT, "TRELAY engine.log").putExtra(Intent.EXTRA_TEXT, EngineLog.tail(this, 200000)), "Журнал TRELAY")));
        clear.setOnClickListener(v -> EngineLog.clear(this));
        body.addView(log, Ui.fill(this, 12));

        LinearLayout about = Ui.card(this);
        about.addView(Ui.caption(this, "О программе"));
        TextView aboutText = Ui.text(this, "TRELAY Android " + TrelayApp.VERSION + "\n" + XrayCore.version() + " (AndroidLibXrayLite, LGPL-3.0)\nMihomo 1.19.31 (GPL-3.0)\nSnakeYAML 2.0 (Apache-2.0)", 12, Ui.MUTED, false);
        aboutText.setPadding(0, Ui.dp(this, 6), 0, 0);
        about.addView(aboutText);
        body.addView(about, Ui.fill(this, 12));

        Button save = Ui.button(this, "Сохранить", true);
        body.addView(save, new LinearLayout.LayoutParams(-1, Ui.dp(this, 46)));
        ((LinearLayout.LayoutParams) save.getLayoutParams()).topMargin = Ui.dp(this, 18);
        save.setOnClickListener(v -> {
            try {
                Model.Config fresh = store.load();
                fresh.settings = collect(sound, output, loud, vibrate);
                fresh.settings.autostart = autostart.isChecked();
                store.save(fresh);
                TrelayVpnService.applyIfRunning(this);
                finish();
            } catch (Exception error) { Ui.error(this, "Не удалось сохранить", error); }
        });
        Ui.fitSystemBars(scroll);
        setContentView(scroll);
    }

    private Model.Settings collect(Switch sound, Spinner output, Switch loud, Switch vibrate) {
        Model.Settings settings = new Model.Settings();
        settings.soundAlerts = sound.isChecked();
        Object item = output.getSelectedItem();
        settings.alertOutput = item instanceof AlertSound.Output ? ((AlertSound.Output) item).id : "speaker";
        settings.maxAlarmVolume = loud.isChecked();
        settings.vibrate = vibrate.isChecked();
        settings.autostart = config.settings.autostart;
        return settings;
    }
}
