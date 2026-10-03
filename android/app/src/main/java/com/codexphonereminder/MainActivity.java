package com.codexphonereminder;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;
import android.widget.ArrayAdapter;

import org.json.JSONArray;
import org.json.JSONObject;
import java.lang.ref.WeakReference;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

@SuppressLint("SetTextI18n")
public final class MainActivity extends Activity {
    static final String EXTRA_TASK_ID = "task_id";
    private static WeakReference<MainActivity> activeActivity = new WeakReference<>(null);
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private SecureStore store;
    private ApiClient api;
    private LinearLayout root;
    private ProgressBar progress;
    private String currentTaskId;
    private int taskViewGeneration;
    private boolean destroyed;
    private String currentProgressAfter;
    private String renderedMessagesSignature;
    private String renderedApprovalId;
    private final ArrayList<String> currentProgressLines = new ArrayList<>();
    private final Set<String> currentProgressIds = new HashSet<>();

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        store = new SecureStore(this);
        api = new ApiClient(store);
        root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(18), dp(18), dp(18), dp(18));
        root.setBackgroundColor(Color.rgb(248, 250, 252));
        progress = new ProgressBar(this);
        progress.setVisibility(View.GONE);
        setContentView(root);
        if (Build.VERSION.SDK_INT >= 33) {
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::handleBack);
        }
        if (store.paired()) showTasks(); else showPairing();
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        String id = intent.getStringExtra(EXTRA_TASK_ID);
        if (id != null && store.paired()) showTask(id);
    }

    @Override protected void onStart() {
        super.onStart();
        activeActivity = new WeakReference<>(this);
        if (!store.paired()) showPairing();
    }

    @Override protected void onStop() {
        if (activeActivity.get() == this) activeActivity.clear();
        super.onStop();
    }

    static void pairingExpired() {
        MainActivity activity = activeActivity.get();
        if (activity != null) activity.runOnUiThread(() -> activity.resetPairing("电脑授权已失效，请重新输入六位配对码"));
    }

    @Override protected void onDestroy() {
        destroyed = true;
        mainHandler.removeCallbacksAndMessages(null);
        worker.shutdownNow();
        super.onDestroy();
    }

    @SuppressLint("GestureBackNavigation")
    @Override public void onBackPressed() { handleBack(); }

    private void handleBack() {
        if (currentTaskId != null) showTasks(); else finish();
    }

    private void showPairing() {
        currentTaskId = null; clear();
        title("连接电脑");
        root.addView(body("首次配对时先在电脑打开 /api/pair，再输入完整 HTTPS 地址和 6 位配对码。地址末尾包含电脑证书指纹，请勿删减；电脑启用云中继后，后续连接会在本地不可达时自动回退。"));
        EditText address = input("安全代理地址，例如 https://192.168.1.10:5188#指纹");
        EditText code = input("6 位配对码");
        code.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        Button pair = button("安全配对");
        root.addView(address); root.addView(code); root.addView(pair);
        pair.setOnClickListener(v -> {
            String a = address.getText().toString().trim(), c = code.getText().toString().trim();
            if (a.isEmpty() || c.length() != 6) { toast("请输入代理地址和 6 位配对码"); return; }
            busy(true);
            worker.execute(() -> {
                try {
                    JSONObject result = api.claim(a, c);
                    store.savePairing(a, result.getString("token"), result.optString("fingerprint"), result.optJSONObject("relay"));
                    getSharedPreferences("notification_states", MODE_PRIVATE).edit().clear().apply();
                    runOnUiThread(() -> { busy(false); startMonitoring(); showTasks(); });
                } catch (Exception e) { fail("配对失败", e); }
            });
        });
        root.addView(progress);
    }

    private void showTasks() {
        currentTaskId = null; taskViewGeneration++; clear();
        LinearLayout bar = horizontal();
        TextView heading = titleView("项目对话");
        bar.addView(heading, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        Button rePair = smallButton("重新配对"); bar.addView(rePair);
        Button refresh = smallButton("刷新"); bar.addView(refresh);
        root.addView(bar);
        TextView status = body("正在连接电脑…");
        root.addView(status); root.addView(progress);
        ScrollView taskScroll = new ScrollView(this);
        LinearLayout taskList = new LinearLayout(this);
        taskList.setOrientation(LinearLayout.VERTICAL);
        taskScroll.addView(taskList);
        root.addView(taskScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));
        refresh.setOnClickListener(v -> loadTasks(status, taskList));
        rePair.setOnClickListener(v -> resetPairing("请输入新的六位配对码"));
        loadTasks(status, taskList);
        startMonitoring();
    }

    private void loadTasks(TextView status, LinearLayout taskList) {
        busy(true);
        worker.execute(() -> {
            try {
                JSONObject health = api.health();
                JSONArray tasks = api.tasks();
                runOnUiThread(() -> {
                    busy(false);
                    status.setText("电脑在线 · " + ("relay".equals(api.connectionMode()) ? "云中继" : "本地直连") +
                        " · CLI " + (health.optBoolean("cliAvailable") ? "可用" : "不可用") +
                        " · 已映射 " + health.optInt("mappedWorkspaces") + " 个工作区\n指纹 " + store.fingerprint());
                    renderTaskCards(tasks, status, taskList);
                    String requested = getIntent().getStringExtra(EXTRA_TASK_ID);
                    if (requested != null) { getIntent().removeExtra(EXTRA_TASK_ID); showTask(requested); }
                });
            } catch (Exception e) { fail("无法读取任务", e); }
        });
    }

    private void renderTaskCards(JSONArray tasks, TextView status, LinearLayout taskList) {
        taskList.removeAllViews();
        if (tasks.length() == 0) { taskList.addView(body("暂无 Codex 对话")); return; }
        Map<String, List<JSONObject>> groups = new LinkedHashMap<>();
        for (int i = 0; i < tasks.length(); i++) {
            JSONObject task = tasks.optJSONObject(i); if (task == null) continue;
            String project = task.optString("project", "未命名项目");
            groups.computeIfAbsent(project, ignored -> new ArrayList<>()).add(task);
        }
        for (Map.Entry<String, List<JSONObject>> group : groups.entrySet()) {
            TextView groupTitle = text("📁 " + group.getKey() + "  ·  " + group.getValue().size() + " 个对话", 16, true);
            groupTitle.setPadding(dp(4), dp(16), 0, dp(8)); taskList.addView(groupTitle);
            for (JSONObject task : group.getValue()) {
            LinearLayout card = card();
            LinearLayout titleRow = horizontal();
            TextView name = text(stateLabel(task.optString("state")), 16, true);
            titleRow.addView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            Button archive = smallButton("归档"); titleRow.addView(archive);
            TextView summary = text(task.optString("title"), 15, false);
            summary.setMaxLines(3); summary.setEllipsize(android.text.TextUtils.TruncateAt.END);
            card.addView(titleRow); card.addView(summary);
            String id = task.optString("id"); card.setOnClickListener(v -> showTask(id));
            archive.setOnClickListener(v -> confirmArchive(id, task.optString("title"), status, taskList));
            taskList.addView(card);
            }
        }
    }

    private void confirmArchive(String id, String title, TextView status, LinearLayout taskList) {
        new AlertDialog.Builder(this).setTitle("归档对话")
            .setMessage("归档后将从任务列表隐藏：\n" + title)
            .setNegativeButton("取消", null)
            .setPositiveButton("归档", (dialog, which) -> worker.execute(() -> {
                try { api.archive(id); runOnUiThread(() -> { toast("对话已归档"); loadTasks(status, taskList); }); }
                catch (Exception error) { fail("归档失败", error); }
            })).show();
    }

    private void showTask(String id) {
        currentTaskId = id; int generation = ++taskViewGeneration; clear();
        currentProgressAfter = null; currentProgressLines.clear(); currentProgressIds.clear();
        renderedMessagesSignature = null;
        renderedApprovalId = null;
        Button back = smallButton("← 返回项目"); back.setOnClickListener(v -> showTasks()); root.addView(back);
        TextView heading = titleView("对话详情"); root.addView(heading);
        TextView content = body("正在载入…");
        root.addView(content);
        TextView liveProgress = body("实时进度 · 正在建立按需连接…");
        liveProgress.setPadding(dp(12), dp(10), dp(12), dp(10));
        liveProgress.setBackgroundColor(Color.rgb(226, 232, 240));
        root.addView(liveProgress);
        LinearLayout approvalPanel = new LinearLayout(this);
        approvalPanel.setOrientation(LinearLayout.VERTICAL);
        root.addView(approvalPanel);
        ScrollView scroll = new ScrollView(this);
        LinearLayout messages = new LinearLayout(this); messages.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(messages); root.addView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));
        LinearLayout modelRow = horizontal();
        TextView modelLabel = text("模型：", 14, true); modelRow.addView(modelLabel);
        Spinner model = new Spinner(this);
        modelRow.addView(model, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        root.addView(modelRow);
        loadModels(model, id, generation);
        LinearLayout composer = horizontal();
        EditText reply = input("发送下一条指令"); composer.addView(reply, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        Button send = button("发送"); composer.addView(send); root.addView(composer); root.addView(progress);
        send.setOnClickListener(v -> {
            String message = reply.getText().toString().trim(); if (message.isEmpty()) return;
            send.setEnabled(false); busy(true);
            worker.execute(() -> {
                try { api.reply(id, message, selectedModel(model)); runOnUiThread(() -> { toast("已交给 Codex CLI"); showTask(id); }); }
                catch (Exception e) { runOnUiThread(() -> send.setEnabled(true)); fail("发送失败", e); }
            });
        });
        busy(true);
        loadTask(id, generation, content, liveProgress, approvalPanel, messages, scroll, true);
    }

    private void loadTask(String id, int generation, TextView header, TextView liveProgress, LinearLayout approvalPanel, LinearLayout messages, ScrollView scroll, boolean firstLoad) {
        worker.execute(() -> {
            try {
                JSONObject task = api.task(id);
                JSONObject fetchedProgress = null;
                try {
                    fetchedProgress = api.progress(id, currentProgressAfter);
                } catch (Exception progressError) {
                    if (isUnauthorized(progressError)) throw progressError;
                }
                JSONObject progressBatch = fetchedProgress;
                runOnUiThread(() -> {
                    if (destroyed || generation != taskViewGeneration || !id.equals(currentTaskId)) return;
                    busy(false);
                    boolean wasAtBottom = firstLoad || scroll.getScrollY() + scroll.getHeight() >= messages.getHeight() - dp(48);
                    boolean messagesChanged = renderTask(task, header, messages);
                    renderApproval(task, approvalPanel, id);
                    if (progressBatch != null) renderProgress(progressBatch, liveProgress);
                    else if (firstLoad) liveProgress.setText("实时进度暂时不可用 · 消息仍在同步");
                    if (messagesChanged && wasAtBottom) scroll.post(() -> scroll.fullScroll(View.FOCUS_DOWN));
                    mainHandler.postDelayed(() -> {
                        if (!destroyed && generation == taskViewGeneration && id.equals(currentTaskId)) loadTask(id, generation, header, liveProgress, approvalPanel, messages, scroll, false);
                    }, 2000);
                });
            } catch (Exception e) {
                runOnUiThread(() -> {
                    if (destroyed || generation != taskViewGeneration || !id.equals(currentTaskId)) return;
                    busy(false);
                    if (isUnauthorized(e)) { resetPairing("电脑授权已失效，请重新输入六位配对码"); return; }
                    if (firstLoad) toast("无法载入对话：" + e.getMessage());
                    mainHandler.postDelayed(() -> {
                        if (!destroyed && generation == taskViewGeneration && id.equals(currentTaskId)) loadTask(id, generation, header, liveProgress, approvalPanel, messages, scroll, false);
                    }, 2000);
                });
            }
        });
    }

    private void loadModels(Spinner spinner, String taskId, int generation) {
        worker.execute(() -> {
            try {
                JSONObject catalog = api.models();
                JSONObject task = api.task(taskId);
                JSONArray models = catalog.optJSONArray("models");
                ArrayList<String> labels = new ArrayList<>(), ids = new ArrayList<>();
                String defaultModel = catalog.optString("defaultModel");
                String preferredModel = task.optString("preferredModel");
                labels.add(defaultModel.isEmpty() ? "默认（电脑配置）" : "默认（" + defaultModel + "）"); ids.add("");
                if (models != null) for (int i = 0; i < models.length(); i++) {
                    JSONObject item = models.optJSONObject(i); if (item == null) continue;
                    String modelId = item.optString("id"), name = item.optString("name", modelId);
                    labels.add(name.equals(modelId) ? modelId : name + "  ·  " + modelId); ids.add(modelId);
                }
                runOnUiThread(() -> {
                    if (destroyed || generation != taskViewGeneration || !taskId.equals(currentTaskId)) return;
                    spinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, labels));
                    spinner.setTag(ids);
                    int selected = ids.indexOf(preferredModel);
                    if (selected >= 0) spinner.setSelection(selected);
                });
            } catch (Exception error) {
                runOnUiThread(() -> {
                    ArrayList<String> labels = new ArrayList<>(), ids = new ArrayList<>();
                    labels.add("默认（电脑配置）"); ids.add("");
                    spinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, labels));
                    spinner.setTag(ids);
                });
            }
        });
    }

    @SuppressWarnings("unchecked")
    private String selectedModel(Spinner spinner) {
        Object tag = spinner.getTag();
        if (!(tag instanceof ArrayList)) return "";
        ArrayList<String> ids = (ArrayList<String>) tag;
        int position = spinner.getSelectedItemPosition();
        return position >= 0 && position < ids.size() ? ids.get(position) : "";
    }

    private void renderApproval(JSONObject task, LinearLayout panel, String taskId) {
        JSONObject approval = task.optJSONObject("approval");
        String approvalId = approval == null ? null : approval.optString("id");
        if (approvalId == null || approvalId.isEmpty()) {
            if (renderedApprovalId != null) { panel.removeAllViews(); renderedApprovalId = null; }
            return;
        }
        if (approvalId.equals(renderedApprovalId)) return;
        renderedApprovalId = approvalId;
        panel.removeAllViews();
        TextView detail = text("权限请求 · 风险 " + approval.optString("risk") + "\n" +
            approval.optString("action") + "\n原因：" + approval.optString("reason") +
            "\n有效期至：" + approval.optString("expiresAt"), 14, true);
        detail.setTextIsSelectable(true); detail.setPadding(dp(12), dp(10), dp(12), dp(10));
        detail.setBackgroundColor(Color.rgb(254, 243, 199)); panel.addView(detail);
        LinearLayout actions = horizontal();
        Button reject = button("拒绝"); Button approve = button("批准");
        actions.addView(reject, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        actions.addView(approve, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        panel.addView(actions);
        String nonce = approval.optString("nonce"), risk = approval.optString("risk");
        reject.setOnClickListener(v -> submitApproval(taskId, approvalId, nonce, false, false));
        approve.setOnClickListener(v -> {
            if ("High".equalsIgnoreCase(risk)) {
                new AlertDialog.Builder(this).setTitle("确认高风险批准")
                    .setMessage("该操作被标记为高风险。确认允许 Codex 继续处理？")
                    .setNegativeButton("取消", null)
                    .setPositiveButton("确认批准", (dialog, which) -> submitApproval(taskId, approvalId, nonce, true, true)).show();
            } else submitApproval(taskId, approvalId, nonce, true, false);
        });
    }

    private void submitApproval(String taskId, String approvalId, String nonce, boolean approved, boolean highRiskConfirmed) {
        busy(true);
        worker.execute(() -> {
            try {
                api.decide(approvalId, nonce, approved, highRiskConfirmed);
                runOnUiThread(() -> { busy(false); toast(approved ? "权限请求已批准" : "权限请求已拒绝"); showTask(taskId); });
            } catch (Exception error) {
                runOnUiThread(() -> { busy(false); if (isUnauthorized(error)) resetPairing("电脑授权已失效，请重新配对"); else { toast("处理审批失败：" + error.getMessage()); showTask(taskId); } });
            }
        });
    }

    private void renderProgress(JSONObject batch, TextView view) {
        JSONArray events = batch.optJSONArray("events");
        if (events != null) for (int i = 0; i < events.length(); i++) {
            JSONObject event = events.optJSONObject(i); if (event == null) continue;
            String eventId = event.optString("id");
            if (!currentProgressIds.add(eventId)) continue;
            currentProgressLines.add("• " + event.optString("summary", "状态已更新"));
            while (currentProgressLines.size() > 6) currentProgressLines.remove(0);
        }
        String cursor = batch.optString("nextCursor", ""); if (!cursor.isEmpty()) currentProgressAfter = cursor;
        StringBuilder text = new StringBuilder("实时进度 · 仅在当前对话打开时同步");
        if (currentProgressLines.isEmpty()) text.append("\n• 等待新的结构化活动");
        else for (String line : currentProgressLines) text.append("\n").append(line);
        view.setText(text.toString());
    }

    private boolean renderTask(JSONObject task, TextView header, LinearLayout messages) {
        header.setText(task.optString("project") + " · " + stateLabel(task.optString("state")) + "\n" + task.optString("title"));
        JSONArray list = task.optJSONArray("messages");
        String signature = messageSignature(list);
        if (signature.equals(renderedMessagesSignature) || hasActiveTextSelection(messages)) return false;
        messages.removeAllViews();
        renderedMessagesSignature = signature;
        if (list == null || list.length() == 0) { messages.addView(text("还没有可显示的对话消息。", 15, false)); return true; }
        for (int i = 0; i < list.length(); i++) {
            JSONObject msg = list.optJSONObject(i); if (msg == null) continue;
            boolean user = "user".equalsIgnoreCase(msg.optString("role"));
            TextView bubble = text((user ? "你\n" : "Codex\n") + msg.optString("text"), 15, false);
            bubble.setTextIsSelectable(true); bubble.setPadding(dp(14), dp(10), dp(14), dp(10));
            bubble.setBackgroundColor(user ? Color.rgb(219, 234, 254) : Color.WHITE);
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            lp.setMargins(0, 0, 0, dp(10)); messages.addView(bubble, lp);
        }
        return true;
    }

    private String messageSignature(JSONArray list) {
        if (list == null || list.length() == 0) return "empty";
        StringBuilder signature = new StringBuilder();
        for (int i = 0; i < list.length(); i++) {
            JSONObject message = list.optJSONObject(i); if (message == null) continue;
            signature.append(message.optString("id")).append('\u0000')
                .append(message.optString("role")).append('\u0000')
                .append(message.optString("text")).append('\u0001');
        }
        return signature.toString();
    }

    private boolean hasActiveTextSelection(LinearLayout messages) {
        for (int i = 0; i < messages.getChildCount(); i++) {
            View child = messages.getChildAt(i);
            if (!(child instanceof TextView)) continue;
            TextView text = (TextView) child;
            int start = text.getSelectionStart(), end = text.getSelectionEnd();
            if (start >= 0 && end > start) return true;
        }
        return false;
    }

    private void startMonitoring() {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 7);
        Intent service = new Intent(this, ReminderService.class);
        startForegroundService(service);
    }

    private boolean isUnauthorized(Exception error) {
        return error instanceof ApiClient.ApiException && ((ApiClient.ApiException) error).status == 401;
    }

    private void resetPairing(String message) {
        taskViewGeneration++;
        currentTaskId = null;
        store.clear();
        getSharedPreferences("notification_states", MODE_PRIVATE).edit().clear().apply();
        stopService(new Intent(this, ReminderService.class));
        showPairing();
        toast(message);
    }

    private void clear() { root.removeAllViews(); }
    private void busy(boolean value) { progress.setVisibility(value ? View.VISIBLE : View.GONE); }
    private void fail(String label, Exception e) { runOnUiThread(() -> { busy(false); if (isUnauthorized(e)) resetPairing("电脑授权已失效，请重新输入六位配对码"); else toast(label + "：" + e.getMessage()); }); }
    private void toast(String message) { Toast.makeText(this, message, Toast.LENGTH_LONG).show(); }
    private int dp(int value) { return (int) (value * getResources().getDisplayMetrics().density + .5f); }
    private void title(String value) { root.addView(titleView(value)); }
    private TextView titleView(String value) { TextView v = text(value, 27, true); v.setPadding(0, dp(6), 0, dp(14)); return v; }
    private TextView body(String value) { TextView v = text(value, 15, false); v.setPadding(0, dp(6), 0, dp(14)); return v; }
    private TextView text(String value, int sp, boolean bold) { TextView v = new TextView(this); v.setText(value); v.setTextSize(sp); v.setTextColor(Color.rgb(15,23,42)); if (bold) v.setTypeface(null, Typeface.BOLD); return v; }
    private EditText input(String hint) { EditText v = new EditText(this); v.setHint(hint); v.setTextSize(15); v.setSingleLine(false); return v; }
    private Button button(String value) { Button b = new Button(this); b.setText(value); return b; }
    private Button smallButton(String value) { Button b = new Button(this); b.setText(value); b.setAllCaps(false); return b; }
    private LinearLayout horizontal() { LinearLayout v = new LinearLayout(this); v.setOrientation(LinearLayout.HORIZONTAL); v.setGravity(Gravity.CENTER_VERTICAL); return v; }
    private LinearLayout card() { LinearLayout v = new LinearLayout(this); v.setOrientation(LinearLayout.VERTICAL); v.setPadding(dp(14), dp(12), dp(14), dp(12)); v.setBackgroundColor(Color.WHITE); LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT); lp.setMargins(0,0,0,dp(10)); v.setLayoutParams(lp); return v; }
    private static String stateLabel(String state) {
        switch (state) { case "Running": return "运行中"; case "WaitingApproval": return "等待批准"; case "WaitingReply": return "等待回复"; case "Completed": return "已完成"; case "Failed": return "失败"; case "Offline": return "离线"; default: return state; }
    }
}
