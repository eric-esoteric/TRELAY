package com.trelay.android;

import android.app.Activity;
import android.os.Bundle;
import android.util.Base64;
import android.util.Log;
import java.nio.charset.StandardCharsets;
import java.util.Map;
import org.json.JSONObject;

// adb shell am start -n com.trelay.android/.DebugControlActivity --es cmd import --es config_b64 <base64 json>
// adb shell am start -n com.trelay.android/.DebugControlActivity --es cmd start|stop|status
public final class DebugControlActivity extends Activity {
    private static final String TAG = "TRELAY_DEBUG";

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        TrelayApp.init(this);
        String cmd = getIntent().getStringExtra("cmd");
        try {
            if ("import".equals(cmd)) {
                String json = new String(Base64.decode(getIntent().getStringExtra("config_b64"), Base64.DEFAULT), StandardCharsets.UTF_8);
                new ConfigStore(this).save(Model.Config.fromJson(new JSONObject(json)));
                TrelayVpnService.applyIfRunning(this);
                Log.i(TAG, "IMPORTED");
            } else if ("start".equals(cmd)) {
                TrelayVpnService.start(this);
                Log.i(TAG, "STARTED");
            } else if ("stop".equals(cmd)) {
                TrelayVpnService.stop(this);
                Log.i(TAG, "STOPPED");
            } else if ("status".equals(cmd)) {
                TunnelEngine engine = TrelayVpnService.engine();
                if (engine == null) Log.i(TAG, "STATUS OFF");
                else {
                    StringBuilder text = new StringBuilder("STATUS ").append(engine.state()).append(" router=").append(engine.routerReady()).append(" | ").append(engine.statusText());
                    for (Map.Entry<String, TunnelEngine.ServerInfo> entry : engine.serverInfo().entrySet())
                        text.append(" | ").append(entry.getKey()).append('=').append(entry.getValue().status).append(':').append(entry.getValue().detail);
                    Log.i(TAG, text.toString());
                }
            }
        } catch (Exception error) {
            Log.e(TAG, "FAILED " + cmd + " " + error);
        }
        finish();
    }
}
