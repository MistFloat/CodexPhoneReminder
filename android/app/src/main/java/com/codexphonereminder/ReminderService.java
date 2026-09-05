package com.codexphonereminder;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.IBinder;

import org.json.JSONArray;
import org.json.JSONObject;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public final class ReminderService extends Service {
    private static final String MONITOR_CHANNEL = "monitor";
    private static final String ACTION_CHANNEL = "codex_actions";
    private static final int FOREGROUND_ID = 1;
    private static final int CONNECTION_ID = 3;
    private ScheduledExecutorService scheduler;

    @Override public void onCreate() {
        super.onCreate();
        createChannels();
        startForeground(FOREGROUND_ID, foregroundNotification("正在等待 Codex 状态变化"));
        scheduler = Executors.newSingleThreadScheduledExecutor();
        scheduler.scheduleWithFixedDelay(this::poll, 0, 10, TimeUnit.SECONDS);
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) { return START_STICKY; }
    @Override public IBinder onBind(Intent intent) { return null; }
    @Override public void onDestroy() { if (scheduler != null) scheduler.shutdownNow(); super.onDestroy(); }

    private void poll() {
        SecureStore secure = new SecureStore(this);
        if (!secure.paired()) { stopSelf(); return; }
        SharedPreferences states = getSharedPreferences("notification_states", MODE_PRIVATE);
        try {
            JSONArray tasks = new ApiClient(secure).tasks();
            boolean initialized = states.getBoolean("initialized", false);
            Set<String> currentIds = new HashSet<>();
            SharedPreferences.Editor edit = states.edit();
            for (int i = 0; i < tasks.length(); i++) {
                JSONObject task = tasks.optJSONObject(i); if (task == null) continue;
                String id = task.optString("id"), state = task.optString("state");
                String signature = notificationSignature(task, state);
                currentIds.add(id);
                String key = "task_" + id;
                String previous = states.getString(key, null);
                boolean legacyPrevious = previous != null && !previous.contains("|");
                boolean newlyActionable = previous == null && actionable(state);
                boolean changedAfterInitialization = initialized && previous != null && !legacyPrevious && !previous.equals(signature);
                boolean newAfterInitialization = initialized && previous == null;
                if (!task.optBoolean("muted") && noteworthy(state) &&
                    (newlyActionable || changedAfterInitialization || newAfterInitialization)) notifyTask(task, state);
                edit.putString(key, signature);
            }
            for (Map.Entry<String, ?> entry : states.getAll().entrySet()) {
                String key = entry.getKey();
                if (key.startsWith("task_") && !currentIds.contains(key.substring(5))) edit.remove(key);
            }
            boolean wasDisconnected = states.getBoolean("disconnected", false);
            edit.putBoolean("initialized", true).putInt("failures", 0).putBoolean("disconnected", false).apply();
            if (wasDisconnected) notifyConnection(true);
            getSystemService(NotificationManager.class).notify(FOREGROUND_ID,
                foregroundNotification("电脑在线 · 已同步 " + tasks.length() + " 个对话"));
        } catch (Exception error) {
            if (error instanceof ApiClient.ApiException && ((ApiClient.ApiException) error).status == 401) {
                secure.clear();
                MainActivity.pairingExpired();
                notifyPairingExpired();
                stopSelf();
                return;
            }
            int failures = states.getInt("failures", 0) + 1;
            boolean disconnected = states.getBoolean("disconnected", false);
            states.edit().putInt("failures", failures).apply();
            if (failures >= 2 && !disconnected) {
                states.edit().putBoolean("disconnected", true).apply();
                notifyConnection(false);
            }
            getSystemService(NotificationManager.class).notify(FOREGROUND_ID,
                foregroundNotification("暂时无法连接电脑 · 10 秒后重试"));
        }
    }

    private boolean actionable(String state) {
        return state.equals("WaitingApproval") || state.equals("WaitingReply");
    }

    private boolean noteworthy(String state) {
        return actionable(state) || state.equals("Completed") || state.equals("Failed") || state.equals("Offline");
    }

    private String notificationSignature(JSONObject task, String state) {
        JSONArray events = task.optJSONArray("events");
        if (events != null) for (int i = events.length() - 1; i >= 0; i--) {
            JSONObject event = events.optJSONObject(i); if (event == null) continue;
            String type = event.optString("type");
            boolean matches = (state.equals("WaitingApproval") && type.equals("ApprovalRequested")) ||
                (state.equals("WaitingReply") && type.equals("WaitingReply")) ||
                (state.equals("Completed") && (type.equals("Completed") || type.equals("TurnCompleted"))) ||
                (state.equals("Failed") && (type.equals("Failed") || type.equals("TurnFailed"))) ||
                (state.equals("Offline") && type.equals("Offline"));
            if (matches) return state + "|" + event.optString("id");
        }
        return state + "|" + task.optString("updatedAt");
    }

    private void notifyTask(JSONObject task, String state) {
        String id = task.optString("id");
        Intent open = new Intent(this, MainActivity.class).putExtra(MainActivity.EXTRA_TASK_ID, id)
            .setFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent pending = PendingIntent.getActivity(this, id.hashCode(), open,
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        String detail = task.optString("completionSummary");
        JSONArray events = task.optJSONArray("events");
        if (detail.isEmpty() && events != null && events.length() > 0) {
            JSONObject latest = events.optJSONObject(events.length() - 1);
            if (latest != null) detail = latest.optString("summary");
        }
        if (detail.isEmpty()) detail = task.optString("title");
        Notification notification = new Notification.Builder(this, ACTION_CHANNEL)
            .setSmallIcon(state.equals("Failed") ? android.R.drawable.stat_notify_error : android.R.drawable.stat_notify_chat)
            .setContentTitle(task.optString("project") + " · " + label(state))
            .setContentText(detail)
            .setStyle(new Notification.BigTextStyle().bigText(detail))
            .setContentIntent(pending).setAutoCancel(true).build();
        getSystemService(NotificationManager.class).notify(id.hashCode(), notification);
    }

    private void notifyConnection(boolean online) {
        Intent open = new Intent(this, MainActivity.class).setFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent pending = PendingIntent.getActivity(this, CONNECTION_ID, open,
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Notification notification = new Notification.Builder(this, ACTION_CHANNEL)
            .setSmallIcon(online ? android.R.drawable.stat_notify_sync : android.R.drawable.stat_notify_error)
            .setContentTitle(online ? "Codex 电脑已恢复连接" : "Codex 电脑连接中断")
            .setContentText(online ? "后台同步已自动恢复" : "已连续两次连接失败，将继续自动重试")
            .setContentIntent(pending).setAutoCancel(true).setOnlyAlertOnce(true).build();
        getSystemService(NotificationManager.class).notify(CONNECTION_ID, notification);
    }

    private Notification foregroundNotification(String text) {
        Intent open = new Intent(this, MainActivity.class);
        PendingIntent pending = PendingIntent.getActivity(this, FOREGROUND_ID, open,
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, MONITOR_CHANNEL)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle("Codex Phone Reminder")
            .setContentText(text).setOnlyAlertOnce(true).setOngoing(true).setContentIntent(pending).build();
    }

    private void notifyPairingExpired() {
        Intent open = new Intent(this, MainActivity.class).setFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent pending = PendingIntent.getActivity(this, 2, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Notification notification = new Notification.Builder(this, ACTION_CHANNEL)
            .setSmallIcon(android.R.drawable.stat_notify_error)
            .setContentTitle("Codex 配对已失效")
            .setContentText("点击重新输入电脑显示的六位配对码")
            .setContentIntent(pending).setAutoCancel(true).build();
        getSystemService(NotificationManager.class).notify(2, notification);
    }

    private void createChannels() {
        NotificationManager manager = getSystemService(NotificationManager.class);
        NotificationChannel monitor = new NotificationChannel(MONITOR_CHANNEL, "连接状态", NotificationManager.IMPORTANCE_LOW);
        monitor.setDescription("维持与电脑代理的局域网连接");
        NotificationChannel actions = new NotificationChannel(ACTION_CHANNEL, "Codex 任务提醒", NotificationManager.IMPORTANCE_HIGH);
        actions.setDescription("等待回复、审批、完成和失败等重要状态");
        manager.createNotificationChannel(monitor); manager.createNotificationChannel(actions);
    }

    private static String label(String state) {
        switch (state) {
            case "WaitingApproval": return "等待批准";
            case "WaitingReply": return "等待回复";
            case "Completed": return "已完成";
            case "Failed": return "失败";
            case "Offline": return "电脑离线";
            default: return state;
        }
    }
}
