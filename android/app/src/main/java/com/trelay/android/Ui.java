package com.trelay.android;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.text.InputType;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.Switch;
import android.widget.TextView;

// Dark graphite theme with the warm orange accent of Windows TRELAY.
final class Ui {
    static final int BACKGROUND = Color.rgb(23, 25, 32);
    static final int CARD = Color.rgb(32, 35, 43);
    static final int INPUT = Color.rgb(43, 47, 57);
    static final int TEXT = Color.rgb(242, 244, 247);
    static final int MUTED = Color.rgb(157, 167, 181);
    static final int ACCENT = Color.rgb(228, 137, 80);
    static final int SECONDARY = Color.rgb(239, 180, 130);
    static final int STROKE = Color.rgb(65, 71, 84);
    static final int GOOD = Color.rgb(96, 196, 128);
    static final int WARN = Color.rgb(236, 196, 92);
    static final int BAD = Color.rgb(236, 102, 92);

    private Ui() { }

    static int dp(Context context, float value) { return Math.round(value * context.getResources().getDisplayMetrics().density); }

    static TextView text(Context context, String value, float size, int color, boolean bold) {
        TextView view = new TextView(context);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(color);
        if (bold) view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return view;
    }

    static TextView caption(Context context, String value) {
        TextView view = text(context, value.toUpperCase(java.util.Locale.ROOT), 10, SECONDARY, true);
        view.setLetterSpacing(0.12f);
        return view;
    }

    static GradientDrawable shape(Context context, int fill, int radius, int border) {
        GradientDrawable result = new GradientDrawable();
        result.setColor(fill);
        result.setCornerRadius(dp(context, radius));
        if (border != 0) result.setStroke(dp(context, 1), border);
        return result;
    }

    static Button button(Context context, String label, boolean accent) {
        Button view = new Button(context);
        view.setText(label);
        view.setAllCaps(false);
        view.setTextSize(14);
        view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        view.setTextColor(accent ? BACKGROUND : TEXT);
        view.setBackground(shape(context, accent ? ACCENT : INPUT, 11, 0));
        view.setMinHeight(0);
        view.setMinimumHeight(0);
        view.setMinWidth(0);
        view.setMinimumWidth(0);
        view.setPadding(dp(context, 14), 0, dp(context, 14), 0);
        view.setStateListAnimator(null);
        return view;
    }

    static Button smallButton(Context context, String label) {
        Button view = button(context, label, false);
        view.setTextSize(11);
        view.setPadding(dp(context, 6), 0, dp(context, 6), 0);
        return view;
    }

    /** A row child that shares the leftover width with its siblings, so nothing falls off the edge. */
    static LinearLayout.LayoutParams share(Context context, int height, int leftMargin) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, dp(context, height), 1f);
        params.leftMargin = dp(context, leftMargin);
        return params;
    }

    static LinearLayout card(Context context) {
        LinearLayout card = new LinearLayout(context);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(context, 14), dp(context, 12), dp(context, 14), dp(context, 12));
        card.setBackground(shape(context, CARD, 16, STROKE));
        return card;
    }

    static LinearLayout row(Context context) {
        LinearLayout row = new LinearLayout(context);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        return row;
    }

    static LinearLayout.LayoutParams wrap(Context context, int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        params.topMargin = dp(context, top);
        return params;
    }

    static LinearLayout.LayoutParams fill(Context context, int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        params.topMargin = dp(context, top);
        return params;
    }

    static LinearLayout.LayoutParams sized(Context context, int width, int height, int left) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(width <= 0 ? width : dp(context, width), dp(context, height));
        params.leftMargin = dp(context, left);
        return params;
    }

    static LinearLayout.LayoutParams weight(Context context) {
        return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1);
    }

    static TextView badge(Context context, String value, int color) {
        TextView badge = text(context, value, 10, color, true);
        badge.setPadding(dp(context, 8), dp(context, 5), dp(context, 8), dp(context, 5));
        badge.setBackground(shape(context, INPUT, 8, 0));
        badge.setSingleLine(true);
        return badge;
    }

    static EditText input(Context context, String hint, String value, boolean multiline) {
        EditText input = new EditText(context);
        input.setHint(hint);
        input.setText(value);
        input.setTextColor(TEXT);
        input.setHintTextColor(MUTED);
        input.setTextSize(15);
        input.setBackground(shape(context, INPUT, 10, 0));
        input.setPadding(dp(context, 12), dp(context, 10), dp(context, 12), dp(context, 10));
        if (multiline) {
            input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
            input.setMinLines(3);
            input.setMaxLines(8);
            input.setGravity(Gravity.TOP);
        } else {
            input.setSingleLine(true);
            input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        }
        return input;
    }

    static Switch toggle(Context context, String label, boolean checked) {
        Switch view = new Switch(context);
        view.setText(label);
        view.setTextColor(TEXT);
        view.setTextSize(14);
        view.setChecked(checked);
        view.setPadding(0, dp(context, 6), 0, dp(context, 6));
        return view;
    }

    static TextView ellipsized(TextView view) {
        view.setSingleLine(true);
        view.setEllipsize(TextUtils.TruncateAt.END);
        return view;
    }

    static View space(Context context, int height) {
        View view = new View(context);
        view.setLayoutParams(new LinearLayout.LayoutParams(1, dp(context, height)));
        return view;
    }

    static void error(Activity activity, String title, Throwable error) {
        if (activity.isFinishing()) return;
        String message = error == null ? "" : error.getMessage();
        if (message == null || message.isEmpty()) message = error == null ? "" : error.getClass().getSimpleName();
        new AlertDialog.Builder(activity).setTitle(title).setMessage(message).setPositiveButton("Понятно", null).show();
    }

    static AlertDialog progress(Activity activity, String title, String message) {
        LinearLayout box = new LinearLayout(activity);
        box.setOrientation(LinearLayout.HORIZONTAL);
        box.setGravity(Gravity.CENTER_VERTICAL);
        box.setPadding(dp(activity, 24), dp(activity, 18), dp(activity, 24), dp(activity, 18));
        box.addView(new ProgressBar(activity));
        TextView text = text(activity, message, 14, TEXT, false);
        text.setPadding(dp(activity, 16), 0, 0, 0);
        box.addView(text);
        AlertDialog dialog = new AlertDialog.Builder(activity).setTitle(title).setView(box).setCancelable(false).create();
        dialog.show();
        return dialog;
    }

    static int statusColor(TunnelEngine.ServerStatus status) {
        if (status == null) return MUTED;
        switch (status) {
            case ACTIVE: case STANDBY: return GOOD;
            case STARTING: return WARN;
            default: return BAD;
        }
    }

    static String statusLabel(TunnelEngine.ServerStatus status) {
        if (status == null) return "";
        switch (status) {
            case ACTIVE: return "работает";
            case STANDBY: return "резерв готов";
            case STARTING: return "проверка";
            case UNHEALTHY: return "не отвечает";
            case DOWN: return "остановлен";
            default: return "пропущен";
        }
    }

    /**
     * Keeps the content clear of the status bar and the navigation gestures.
     *
     * Android 15 lays every activity out edge to edge, so without this the title of the screen is
     * drawn under the clock and the last row under the home indicator.
     */
    static void fitSystemBars(final View root) {
        final int paddingTop = root.getPaddingTop();
        final int paddingBottom = root.getPaddingBottom();
        root.setOnApplyWindowInsetsListener(new View.OnApplyWindowInsetsListener() {
            @Override public android.view.WindowInsets onApplyWindowInsets(View view, android.view.WindowInsets insets) {
                int top, bottom;
                if (android.os.Build.VERSION.SDK_INT >= 30) {
                    android.graphics.Insets bars = insets.getInsets(
                            android.view.WindowInsets.Type.systemBars() | android.view.WindowInsets.Type.displayCutout());
                    top = bars.top;
                    bottom = bars.bottom;
                }
                else {
                    top = insets.getSystemWindowInsetTop();
                    bottom = insets.getSystemWindowInsetBottom();
                }
                view.setPadding(view.getPaddingLeft(), paddingTop + top, view.getPaddingRight(), paddingBottom + bottom);
                return insets;
            }
        });
        root.requestApplyInsets();
    }
}
