using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace CodexPhoneReminder.Agent;

public sealed class AgentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskItem> _tasks = [];
    private readonly Dictionary<string, ApprovalRequest> _approvals = [];
    private readonly HashSet<string> _eventIds = [];
    private readonly string _path;
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;
    public NotificationSettings Settings { get; set; } = new();

    public AgentStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "data"); Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "agent-state-v2.json"); Load();
    }

    public object Tasks() { lock (_gate) { PruneExpiredApprovals(); return _tasks.Values.Where(x => !x.Archived).OrderBy(Priority).ThenByDescending(x => x.UpdatedAt).ToArray(); } }
    public object? Task(string id) { lock (_gate) { PruneExpiredApprovals(); return !_tasks.TryGetValue(id, out var t) ? null : new { task = t, approval = _approvals.Values.FirstOrDefault(a => a.TaskId == id && !a.Used && a.ExpiresAt > DateTimeOffset.UtcNow) }; } }
    public ProgressBatch Progress(string id, string? after, DateTimeOffset leaseExpiresAt)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task)) return new(true, leaseExpiresAt, "Offline", [], after);
            var progress = task.Events.Where(e => IsProgressEvent(e.Type)).ToList();
            int start;
            if (string.IsNullOrWhiteSpace(after)) start = Math.Max(0, progress.Count - 30);
            else { var cursor = progress.FindIndex(e => e.Id == after); start = cursor >= 0 ? cursor + 1 : Math.Max(0, progress.Count - 30); }
            var events = progress.Skip(start).Take(50).ToArray();
            return new(true, leaseExpiresAt, task.State.ToString(), events, events.LastOrDefault()?.Id ?? after);
        }
    }
    public ApprovalRequest? Approval(string id) { lock (_gate) return _approvals.GetValueOrDefault(id); }
    public bool MarkRead(string id) { lock (_gate) { if (!_tasks.TryGetValue(id, out var t)) return false; t.Unread = false; Save(); return true; } }
    public bool? ToggleMute(string id) { lock (_gate) { if (!_tasks.TryGetValue(id, out var t)) return null; t.Muted = !t.Muted; Save(); return t.Muted; } }
    public bool Archive(string id) { lock (_gate) { if (!_tasks.TryGetValue(id, out var t)) return false; t.Archived = true; Save(); return true; } }
    public string? PreferredModel(string id) { lock (_gate) return _tasks.GetValueOrDefault(id)?.PreferredModel; }
    public bool SetPreferredModel(string id, string? model)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task)) return false;
            task.PreferredModel = string.IsNullOrWhiteSpace(model) ? null : model;
            Save(); return true;
        }
    }

    public TimelineEvent Add(NewEvent input)
    {
        lock (_gate)
        {
            var eventId = input.Id ?? Guid.NewGuid().ToString("N");
            if (_eventIds.Contains(eventId)) return _tasks[input.TaskId].Events.First(e => e.Id == eventId);
            var now = DateTimeOffset.UtcNow;
            if (!_tasks.TryGetValue(input.TaskId, out var task))
            {
                task = new TaskItem { Id = input.TaskId, Project = Sanitize(input.Project), Title = Sanitize(input.Title), State = TaskState.Running, StartedAt = now, UpdatedAt = now };
                _tasks[input.TaskId] = task;
            }
            else
            {
                if (task.Title == "Codex 对话" && !string.IsNullOrWhiteSpace(input.Title)) task.Title = Sanitize(input.Title);
                if (task.Project == "Codex" && !string.IsNullOrWhiteSpace(input.Project)) task.Project = Sanitize(input.Project);
            }
            var approvalId = input.Type == EventType.ApprovalRequested ? Guid.NewGuid().ToString("N") : null;
            var ev = new TimelineEvent(eventId, input.Type, now, Sanitize(input.Summary), Sanitize(input.Detail), input.Risk, approvalId);
            task.Events.Add(ev); task.UpdatedAt = now; task.State = Map(input.Type); task.Unread = input.Type is not EventType.Started and not EventType.Progress;
            if (input.Type == EventType.Completed) task.CompletionSummary = ev.Summary;
            if (approvalId is not null) _approvals[approvalId] = new ApprovalRequest
            {
                Id = approvalId, TaskId = task.Id, Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                Action = Sanitize(input.Detail ?? input.Summary), Reason = Sanitize(input.Reason ?? "Codex 请求执行此操作"), Risk = input.Risk, ExpiresAt = now.AddMinutes(10)
            };
            _eventIds.Add(eventId); LastActivity = now; Save(); return ev;
        }
    }

    public bool AddMessage(string taskId, ChatMessage message)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task)) return false;
            if (!MergeMessage(task.Messages, message)) return true;
            if (task.Messages.Count > 200) task.Messages.RemoveRange(0, task.Messages.Count - 200);
            if (message.CreatedAt > task.UpdatedAt) task.UpdatedAt = message.CreatedAt;
            Save(); return true;
        }
    }

    public static bool MergeMessage(List<ChatMessage> messages, ChatMessage incoming)
    {
        if (messages.Any(m => m.Id == incoming.Id)) return false;
        var clean = incoming with { Text = Sanitize(incoming.Text) };
        if (!IsOptimisticId(clean.Id))
        {
            var match = messages
                .Select((message, index) => new { message, index, distance = Math.Abs((message.CreatedAt - clean.CreatedAt).TotalSeconds) })
                .Where(x => IsOptimisticId(x.message.Id) && x.message.Role == clean.Role &&
                    NormalizeMessage(x.message.Text) == NormalizeMessage(clean.Text) && x.distance <= 300)
                .OrderBy(x => x.distance)
                .FirstOrDefault();
            if (match is not null)
            {
                messages[match.index] = clean;
                messages.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
                return true;
            }
        }
        messages.Add(clean);
        messages.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        return true;
    }

    public void UpdateMetadata(string taskId, string project, string? title)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task)) return;
            if (!string.IsNullOrWhiteSpace(project)) task.Project = Sanitize(project);
            if (!string.IsNullOrWhiteSpace(title)) task.Title = Sanitize(title);
        }
    }

    public DecisionResult Decide(string id, ApprovalDecision decision)
    {
        lock (_gate)
        {
            if (!_approvals.TryGetValue(id, out var req)) return new(DecisionStatus.NotFound, "审批请求不存在");
            if (req.Used) return new(DecisionStatus.Replayed, "该请求已经处理，不能重复审批");
            if (req.ExpiresAt < DateTimeOffset.UtcNow) { Expire(req); Save(); return new(DecisionStatus.Expired, "审批请求已过期，已从待处理列表移除"); }
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(req.Nonce), System.Text.Encoding.UTF8.GetBytes(decision.Nonce))) return new(DecisionStatus.Invalid, "一次性随机数无效");
            if (decision.Approved && req.Risk == RiskLevel.High && !decision.HighRiskConfirmed) return new(DecisionStatus.Invalid, "高风险操作需要二次确认");
            req.Used = true; req.Result = decision.Approved ? "approved" : "rejected";
            var task = _tasks[req.TaskId]; task.State = decision.Approved ? TaskState.Running : TaskState.Cancelled; task.UpdatedAt = DateTimeOffset.UtcNow;
            task.Events.Add(new(Guid.NewGuid().ToString("N"), decision.Approved ? EventType.Resumed : EventType.Failed, task.UpdatedAt, decision.Approved ? "已在手机批准，任务恢复运行" : "已在手机拒绝请求", null, req.Risk));
            Save(); return new(DecisionStatus.Success, decision.Approved ? "批准成功" : "拒绝成功", decision.Approved);
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(new Snapshot(_tasks.Values.ToList(), _approvals.Values.ToList(), Settings), new JsonSerializerOptions { WriteIndented = true });
            var tmp = _path + ".tmp"; File.WriteAllText(tmp, json); File.Move(tmp, _path, true);
        }
    }
    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path));
            if (snapshot is null) return;
            var cleaned = false;
            foreach (var task in snapshot.Tasks.Where(t => !t.Id.StartsWith("demo-", StringComparison.Ordinal)))
            {
                var originalCount = task.Messages.Count;
                var merged = new List<ChatMessage>();
                foreach (var message in task.Messages.OrderBy(m => m.CreatedAt)) MergeMessage(merged, message);
                task.Messages.Clear(); task.Messages.AddRange(merged);
                cleaned |= task.Messages.Count != originalCount;
                _tasks[task.Id] = task;
                foreach (var item in task.Events) _eventIds.Add(item.Id);
            }
            foreach (var approval in snapshot.Approvals.Where(a => _tasks.ContainsKey(a.TaskId))) _approvals[approval.Id] = approval;
            Settings = snapshot.Settings;
            if (cleaned) Save();
        }
        catch { }
    }
    private static int Priority(TaskItem t) => t.State switch { TaskState.WaitingApproval => 0, TaskState.WaitingReply => 1, TaskState.Failed => 2, TaskState.Completed when t.Unread => 3, TaskState.Running => 4, _ => 5 };
    private void PruneExpiredApprovals()
    {
        var expired = _approvals.Values.Where(a => !a.Used && a.ExpiresAt <= DateTimeOffset.UtcNow).ToArray();
        foreach (var approval in expired) Expire(approval);
        if (expired.Length > 0) Save();
    }
    private void Expire(ApprovalRequest approval)
    {
        approval.Used = true; approval.Result = "expired";
        if (_tasks.TryGetValue(approval.TaskId, out var task) && task.State == TaskState.WaitingApproval)
        {
            task.State = TaskState.Running; task.UpdatedAt = DateTimeOffset.UtcNow;
            task.Events.Add(new(Guid.NewGuid().ToString("N"), EventType.Progress, task.UpdatedAt, "审批请求已失效并移出待处理列表", null, approval.Risk));
        }
    }
    private static TaskState Map(EventType e) => e switch { EventType.ApprovalRequested => TaskState.WaitingApproval, EventType.WaitingReply => TaskState.WaitingReply, EventType.Completed or EventType.TurnCompleted => TaskState.Completed, EventType.Failed or EventType.TurnFailed => TaskState.Failed, EventType.Cancelled => TaskState.Cancelled, EventType.Offline => TaskState.Offline, _ => TaskState.Running };
    private static bool IsProgressEvent(EventType type) => type is EventType.TurnStarted or EventType.ToolStarted or EventType.ToolCompleted or EventType.FilesChanged or EventType.TestsUpdated or EventType.TurnCompleted or EventType.TurnFailed;
    private static bool IsOptimisticId(string id) => id.StartsWith("pending:", StringComparison.Ordinal) || Guid.TryParseExact(id, "N", out _);
    private static string NormalizeMessage(string text) => Sanitize(text).Trim().ReplaceLineEndings("\n");
    public static string Sanitize(string? value) { if (string.IsNullOrWhiteSpace(value)) return ""; var s = Regex.Replace(value, @"(?i)(password|token|secret|api[_-]?key)\s*[=:]\s*[^\s&]+", "$1=***"); s = Regex.Replace(s, @"[A-Za-z]:\\(?:[^\\\s]+\\){1,}[^\s]*", "<工作区路径>"); return s.Length > 2000 ? s[..2000] : s; }
    private sealed record Snapshot(List<TaskItem> Tasks, List<ApprovalRequest> Approvals, NotificationSettings Settings);
}
