package com.trelay.android;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.drawable.Drawable;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.View;
import android.view.ViewGroup;
import android.widget.BaseAdapter;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.Switch;
import android.widget.TextView;
import java.text.Collator;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.function.Consumer;

// Installed applications: labels, icons, and the multi-select picker used by the route editor.
final class AppCatalog {
    static final class App {
        final String packageName; final String label; final boolean launchable; final boolean system;
        App(String packageName, String label, boolean launchable, boolean system) { this.packageName = packageName; this.label = label; this.launchable = launchable; this.system = system; }
    }

    private static final Map<String, String> LABELS = new HashMap<>();

    private AppCatalog() { }

    static String label(Context context, String packageName) {
        synchronized (LABELS) { String cached = LABELS.get(packageName); if (cached != null) return cached; }
        String label;
        try {
            PackageManager manager = context.getPackageManager();
            label = manager.getApplicationLabel(manager.getApplicationInfo(packageName, 0)).toString();
        } catch (PackageManager.NameNotFoundException missing) { label = packageName + " (не установлено)"; }
        synchronized (LABELS) { LABELS.put(packageName, label); }
        return label;
    }

    static Drawable icon(Context context, String packageName) {
        try { return context.getPackageManager().getApplicationIcon(packageName); } catch (Exception error) { return null; }
    }

    static List<App> load(Context context) {
        PackageManager manager = context.getPackageManager();
        Set<String> launchable = new LinkedHashSet<>();
        for (ResolveInfo info : manager.queryIntentActivities(new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0))
            launchable.add(info.activityInfo.packageName);
        List<App> apps = new ArrayList<>();
        for (ApplicationInfo info : manager.getInstalledApplications(0)) {
            if (info.packageName.equals(context.getPackageName())) continue;
            boolean system = (info.flags & ApplicationInfo.FLAG_SYSTEM) != 0 && (info.flags & ApplicationInfo.FLAG_UPDATED_SYSTEM_APP) == 0;
            String label = manager.getApplicationLabel(info).toString();
            synchronized (LABELS) { LABELS.put(info.packageName, label); }
            apps.add(new App(info.packageName, label, launchable.contains(info.packageName), system));
        }
        Collator collator = Collator.getInstance(new Locale("ru"));
        Collections.sort(apps, (a, b) -> collator.compare(a.label, b.label));
        return apps;
    }

    static Bitmap bitmap(Drawable drawable, int size) {
        Bitmap bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(bitmap);
        drawable.setBounds(0, 0, size, size);
        drawable.draw(canvas);
        return bitmap;
    }

    /** Launcher icon for "open through TRELAY": the app icon with an orange TRELAY dot. */
    static Bitmap shortcutIcon(Context context, String packageName) {
        int size = Ui.dp(context, 108);
        Bitmap bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(bitmap);
        Drawable icon = icon(context, packageName);
        int inset = size / 6;
        if (icon != null) { icon.setBounds(inset, inset, size - inset, size - inset); icon.draw(canvas); }
        Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        paint.setColor(Ui.BACKGROUND);
        canvas.drawCircle(size - inset * 1.25f, size - inset * 1.25f, size / 7f, paint);
        paint.setColor(Ui.ACCENT);
        canvas.drawCircle(size - inset * 1.25f, size - inset * 1.25f, size / 9.5f, paint);
        return bitmap;
    }

    static void pick(Activity activity, Set<String> initial, Set<String> elsewhere, Consumer<List<String>> done) {
        AlertDialog loading = Ui.progress(activity, "Приложения", "Загружаю список…");
        new Thread(() -> {
            List<App> apps = load(activity);
            activity.runOnUiThread(() -> {
                loading.dismiss();
                if (!activity.isFinishing()) show(activity, apps, initial, elsewhere, done);
            });
        }, "trelay-apps").start();
    }

    private static void show(Activity activity, List<App> all, Set<String> initial, Set<String> elsewhere, Consumer<List<String>> done) {
        Set<String> selected = new LinkedHashSet<>(initial);
        List<App> visible = new ArrayList<>();
        boolean[] showSystem = {false};
        String[] query = {""};

        LinearLayout box = new LinearLayout(activity);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(Ui.dp(activity, 16), Ui.dp(activity, 8), Ui.dp(activity, 16), 0);
        EditText search = Ui.input(activity, "Поиск по названию", "", false);
        box.addView(search, Ui.fill(activity, 0));
        Switch system = Ui.toggle(activity, "Показать системные приложения", false);
        box.addView(system, Ui.fill(activity, 4));
        ListView list = new ListView(activity);
        list.setDivider(null);
        box.addView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, Ui.dp(activity, 420)));

        BaseAdapter adapter = new BaseAdapter() {
            @Override public int getCount() { return visible.size(); }
            @Override public Object getItem(int position) { return visible.get(position); }
            @Override public long getItemId(int position) { return position; }
            @Override public View getView(int position, View convert, ViewGroup parent) {
                LinearLayout row = convert instanceof LinearLayout ? (LinearLayout) convert : null;
                if (row == null) {
                    row = Ui.row(activity);
                    row.setPadding(0, Ui.dp(activity, 7), 0, Ui.dp(activity, 7));
                    ImageView image = new ImageView(activity);
                    row.addView(image, new LinearLayout.LayoutParams(Ui.dp(activity, 36), Ui.dp(activity, 36)));
                    LinearLayout labels = new LinearLayout(activity);
                    labels.setOrientation(LinearLayout.VERTICAL);
                    labels.setPadding(Ui.dp(activity, 12), 0, Ui.dp(activity, 8), 0);
                    labels.addView(Ui.ellipsized(Ui.text(activity, "", 15, Ui.TEXT, false)));
                    labels.addView(Ui.ellipsized(Ui.text(activity, "", 11, Ui.MUTED, false)));
                    row.addView(labels, Ui.weight(activity));
                    CheckBox check = new CheckBox(activity);
                    check.setClickable(false);
                    check.setFocusable(false);
                    row.addView(check);
                }
                App app = visible.get(position);
                ((ImageView) row.getChildAt(0)).setImageDrawable(icon(activity, app.packageName));
                LinearLayout labels = (LinearLayout) row.getChildAt(1);
                ((TextView) labels.getChildAt(0)).setText(app.label);
                String note = elsewhere.contains(app.packageName) ? "уже в другом маршруте · " : "";
                ((TextView) labels.getChildAt(1)).setText(note + app.packageName);
                ((CheckBox) row.getChildAt(2)).setChecked(selected.contains(app.packageName));
                return row;
            }
        };
        list.setAdapter(adapter);
        Runnable filter = () -> {
            visible.clear();
            String needle = query[0].toLowerCase(Locale.ROOT);
            for (App app : all) {
                boolean match = needle.isEmpty() || app.label.toLowerCase(Locale.ROOT).contains(needle) || app.packageName.contains(needle);
                if (!match) continue;
                if (!showSystem[0] && !app.launchable && !selected.contains(app.packageName)) continue;
                visible.add(app);
            }
            // Selected applications first so the current choice is always visible.
            Collections.sort(visible, (a, b) -> Boolean.compare(!selected.contains(a.packageName), !selected.contains(b.packageName)));
            adapter.notifyDataSetChanged();
        };
        list.setOnItemClickListener((parent, view, position, id) -> {
            App app = visible.get(position);
            if (!selected.remove(app.packageName)) selected.add(app.packageName);
            adapter.notifyDataSetChanged();
        });
        search.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) { }
            @Override public void afterTextChanged(Editable s) { query[0] = s.toString().trim(); filter.run(); }
        });
        system.setOnCheckedChangeListener((button, checked) -> { showSystem[0] = checked; filter.run(); });
        filter.run();
        new AlertDialog.Builder(activity).setTitle("Приложения маршрута").setView(box)
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Готово", (dialog, which) -> done.accept(new ArrayList<>(selected))).show();
    }
}
