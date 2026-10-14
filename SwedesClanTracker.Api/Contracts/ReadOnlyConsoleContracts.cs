using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Contracts;

/// <summary>Authenticated application session information for the operator console.</summary>
public sealed record AuthSessionResponse(
    string Username,
    IReadOnlyList<string> Roles,
    bool IsAdmin);

public sealed record ConsoleOverviewResponse(
    DateTimeOffset ServerTime,
    ConsoleHealthSummaryResponse Health,
    IReadOnlyList<ConsoleWorkLaneResponse> OpenWork,
    IReadOnlyList<ConsoleAttentionResponse> RosterAttention,
    IReadOnlyList<ConsoleRecentChangeResponse> RecentChanges);

public sealed record ConsoleHealthSummaryResponse(
    string Overall,
    ConsoleHealthComponentResponse Api,
    ConsoleHealthComponentResponse Worker,
    ConsoleHealthComponentResponse Database,
    ConsoleHealthComponentResponse LatestSync,
    ConsoleReadinessSummaryResponse Readiness);

public sealed record ConsoleHealthComponentResponse(
    string State,
    string Detail,
    DateTimeOffset? ObservedAt,
    string? RelatedPlayer);

public sealed record ConsoleReadinessSummaryResponse(
    string State,
    string Detail,
    int ConfiguredCount,
    int RequiredCount);

public sealed record ConsoleWorkLaneResponse(
    string Lane,
    string Label,
    int Count,
    DateTimeOffset? OldestAt,
    string Tone,
    string Route);

public sealed record ConsoleAttentionResponse(
    string Kind,
    string Label,
    int Count,
    DateTimeOffset? OldestAt,
    string Tone,
    string Route);

public sealed record ConsoleRecentChangeResponse(
    string EventId,
    string EventType,
    string Label,
    string Detail,
    string Status,
    DateTimeOffset OccurredAt,
    string? PlayerId,
    string? PlayerName,
    string? Source,
    string? CorrelationId,
    string? CaseId);

public sealed record ConsoleRosterResponse(
    DateTimeOffset ServerTime,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<ConsoleRosterRowResponse> Items);

public sealed record ConsoleRosterRowResponse(
    int PlayerId,
    string Username,
    string CurrentRank,
    string EligibleRank,
    string Status,
    DateTimeOffset? LastSynced,
    DateTimeOffset LastSeen,
    string Freshness,
    IReadOnlyList<string> Attention,
    ConsoleSnapshotResponse? LatestSnapshot);

public sealed record ConsoleSnapshotResponse(
    DateTimeOffset Timestamp,
    int TotalLevel,
    double Ehb,
    double Ehp,
    int Collections,
    int PetCount);

public sealed record ConsoleProfileResponse(
    int PlayerId,
    string Username,
    string CurrentRank,
    string EligibleRank,
    string Status,
    DateTimeOffset? LastSynced,
    DateTimeOffset LastSeen,
    string Freshness,
    ConsoleSnapshotResponse? LatestSnapshot,
    IReadOnlyList<ConsoleOpenCaseResponse> OpenCases,
    IReadOnlyList<ConsoleLifecycleEventResponse> LifecycleEvents,
    string TrendRange,
    IReadOnlyList<ConsoleTrendPointResponse> Trend,
    IReadOnlyList<string> SupportedTrendRanges);

public sealed record ConsoleTrendPointResponse(
    DateTimeOffset Timestamp,
    int TotalLevel,
    double Ehb,
    double Ehp,
    int Collections,
    int PetCount);

public sealed record ConsoleOpenCaseResponse(
    string CaseId,
    string Family,
    string State,
    string Summary,
    DateTimeOffset OpenedAt);

public sealed record ConsoleLifecycleEventResponse(
    string EventId,
    string EventType,
    string Label,
    string Status,
    DateTimeOffset CreatedAt,
    string? Source,
    string? CorrelationId,
    string? CaseId,
    IReadOnlyList<ConsoleEvidenceResponse> Evidence);

public sealed record ConsoleReviewQueueResponse(
    DateTimeOffset ServerTime,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<ConsoleReviewLaneResponse> Lanes,
    IReadOnlyList<ConsoleReviewCaseResponse> Items);

public sealed record ConsoleReviewLaneResponse(
    string Family,
    string Label,
    int Count,
    string Tone);

public sealed record ConsoleReviewCaseResponse(
    string CaseId,
    string Family,
    string State,
    string Subject,
    int? PlayerId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ObservedAt,
    string Summary,
    string Status,
    string? Source,
    string? CaseVersion,
    IReadOnlyList<ConsoleEvidenceResponse> Evidence,
    IReadOnlyList<string> Observations,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ConsoleActionDescriptorResponse> Actions,
    IReadOnlyList<ConsoleRelatedCaseResponse> RelatedCases,
    IReadOnlyList<ConsoleRelatedCaseResponse> SupersededCases);

public sealed record ConsoleEvidenceResponse(
    string Label,
    string? Value,
    string State,
    DateTimeOffset? ObservedAt);

public sealed record ConsoleActionDescriptorResponse(
    string ActionId,
    string Label,
    string Direction,
    bool Interactive,
    bool RequiresPreflight,
    bool RequiresExactRsn,
    string? DisabledReason);

public sealed record ConsoleRelatedCaseResponse(
    string CaseId,
    string Family,
    string Relationship,
    string Subject);

public sealed record ConsoleReviewDetailResponse(
    ConsoleReviewCaseResponse Case,
    string Precedence,
    IReadOnlyList<string> SuppressedSignals,
    string GateState,
    bool CanExecute);

public sealed record ConsoleIgnoredPoliciesResponse(
    DateTimeOffset ServerTime,
    IReadOnlyList<ConsoleIgnoredPolicyResponse> Items);

public sealed record ConsoleIgnoredPolicyResponse(
    string PolicyId,
    string Family,
    string Subject,
    DateTimeOffset IgnoredAt,
    string Status,
    string? Source,
    ConsoleActionDescriptorResponse ResumeDescriptor);

public sealed record ConsoleActivityResponse(
    DateTimeOffset ServerTime,
    int TotalCount,
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<ConsoleActivityEventResponse> Items);

public sealed record ConsoleActivityEventResponse(
    string EventId,
    string EventType,
    string Label,
    string Status,
    DateTimeOffset OccurredAt,
    int? PlayerId,
    string? PlayerName,
    string? Source,
    string? CorrelationId,
    string? CaseId,
    IReadOnlyList<ConsoleEvidenceResponse> Evidence);

public sealed record ConsoleSystemResponse(
    DateTimeOffset ServerTime,
    ConsoleHealthSummaryResponse Health,
    IReadOnlyList<ConsoleSystemCheckResponse> Checks,
    IReadOnlyList<ConsoleConfigurationCheckResponse> Configuration);

public sealed record ConsoleSystemCheckResponse(
    string Component,
    string State,
    string Detail,
    DateTimeOffset? ObservedAt);

public sealed record ConsoleConfigurationCheckResponse(
    string Key,
    string State,
    string Detail);
