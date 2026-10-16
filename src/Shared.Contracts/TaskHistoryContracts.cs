namespace MinhHuy.AIOffice.Shared.Contracts;

/// <summary>Owner-only archive metadata. Invalid durable state is unavailable, never repaired into a completion.</summary>
public sealed record TaskHistoryItem(Guid TaskId, TaskExecutionStatus? Status, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, string? Summary, bool MetadataUnavailable);

public sealed record TaskHistoryPage(Guid CompanyId, IReadOnlyList<TaskHistoryItem> Items,
    int Offset, int Limit, bool HasMore);

/// <summary>Allowlisted output from the existing trusted pilot executors; no raw checkpoint or diagnostics.</summary>
public sealed record TaskHistoryResult(string Kind, string? Answer, string Evidence,
    string? Provider, string? Model, int? InputTokens, int? OutputTokens, int? TotalTokens);

public sealed record TaskHistoryDetail(Guid CompanyId, TaskHistoryItem Task,
    TaskHistoryResult? Result, bool ResultUnavailable);
