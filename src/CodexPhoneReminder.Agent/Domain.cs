namespace CodexPhoneReminder.Agent;

public enum TaskState { Running, WaitingApproval, WaitingReply, Completed, Failed, Cancelled, Offline }
public enum EventType { Started, Progress, TurnStarted, ToolStarted, ToolCompleted, FilesChanged, TestsUpdated, TurnCompleted, TurnFailed, ApprovalRequested, WaitingReply, Completed, Failed, Resumed, Cancelled, Offline }
public enum RiskLevel { Low, Medium, High }
public enum DecisionStatus { Success, NotFound, Expired, Replayed, Invalid }

public sealed record NewEvent(string? Id, string TaskId, string Project, string Title, EventType Type,
    string Summary, string? Detail = null, RiskLevel Risk = RiskLevel.Low, string? Reason = null);
public sealed record TimelineEvent(string Id, EventType Type, DateTimeOffset OccurredAt, string Summary,
    string? Detail, RiskLevel Risk, string? ApprovalId = null);
public sealed record ChatMessage(string Id, string Role, string Text, DateTimeOffset CreatedAt);
public sealed class TaskItem
{
    public required string Id { get; init; }
    public required string Project { get; set; }
    public required string Title { get; set; }
    public TaskState State { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Unread { get; set; }
    public bool Muted { get; set; }
    public bool Archived { get; set; }
    public string? PreferredModel { get; set; }
    public string? CompletionSummary { get; set; }
    public string TestStatus { get; set; } = "未运行";
    public int OpenIssues { get; set; }
    public List<TimelineEvent> Events { get; init; } = [];
    public List<ChatMessage> Messages { get; init; } = [];
}
public sealed class ApprovalRequest
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required string Nonce { get; init; }
    public required string Action { get; init; }
    public required string Reason { get; init; }
    public RiskLevel Risk { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public bool Used { get; set; }
    public string? Result { get; set; }
}
public sealed record ApprovalDecision(bool Approved, string Nonce, bool HighRiskConfirmed = false);
public sealed record DecisionResult(DecisionStatus Status, string Message, bool? Approved = null);
public sealed record PairClaim(string Code);
public sealed record TaskReply(string Message, string? Model = null);
public sealed record ProgressBatch(bool Active, DateTimeOffset LeaseExpiresAt, string State, IReadOnlyList<TimelineEvent> Events, string? NextCursor);
public sealed record CliRunInfo(string Id, string ThreadId, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt = null, int? ExitCode = null);
public sealed record PairOffer(string Address, string Code, string Fingerprint, DateTimeOffset ExpiresAt);
public sealed record PairedDevice(string Id, string Name, DateTimeOffset PairedAt, DateTimeOffset LastSeenAt, bool Revoked = false);
public sealed record PairResult(string Token, PairedDevice Device, string Fingerprint);
public sealed record NotificationSettings(bool Approval = true, bool WaitingReply = true, bool Completed = true,
    bool Failed = true, bool LongRunning = true, bool Offline = true, string QuietStart = "23:00", string QuietEnd = "07:00", int LongRunningMinutes = 30);
public sealed class CodexCliOptions
{
    public string Executable { get; set; } = "codex.exe";
    public string SessionsPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
    public int PollSeconds { get; set; } = 2;
}
