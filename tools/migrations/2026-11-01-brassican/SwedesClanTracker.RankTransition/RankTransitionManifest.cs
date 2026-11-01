using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwedesClanTracker.RankTransition;

public enum TransitionOperationKind
{
    DatabaseCurrentRank,
    DatabaseEligibleRank,
    QueuePromotionCandidate,
    SupersedePromotionCandidate,
    WiseOldManRole,
    DiscordRoleRename,
    DiscordMemberRankRole,
    FinalizeApprovedPromotion
}

public enum TransitionOperationStatus
{
    Planned,
    Applied,
    AlreadyApplied,
    Skipped,
    Failed,
    Drifted,
    RolledBack,
    AlreadyRolledBack
}

public sealed class RankTransitionManifest
{
    public const int CurrentSchemaVersion = 2;
    public const string ApprovedCutoverPolicyVersion = "approved-cutover-v1";
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ManifestId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string FrozenMappingVersion { get; init; } = FrozenRankMapping.Version;
    public bool CompleteReadOnlyScope { get; init; } = true;
    /// <summary>
    /// When true, qualifying promotions are committed directly to their final
    /// rank after all required external observations and mutations verify.
    /// The default is false so library callers retain the normal pending-
    /// approval behavior.
    /// </summary>
    public bool ApprovedForCutover { get; init; }
    public string CutoverApprovalPolicyVersion { get; init; } = ApprovedCutoverPolicyVersion;
    /// <summary>
    /// When true, Discord member rank changes are a frozen human-review
    /// roster only. The tool never writes member role membership in this mode;
    /// the CLI uses it for the approved cutover, while library callers may
    /// explicitly opt into the older automatic member mutation path.
    /// </summary>
    public bool DiscordMemberRanksManual { get; init; }
    /// <summary>
    /// When true, WOM is an observed/reconciliation gate only. The frozen
    /// manifest retains planned before/after role evidence, but apply and
    /// rollback never issue WOM mutations. The standard CLI sets this for the
    /// user-managed in-game cutover; library callers retain the old default.
    /// </summary>
    public bool WomUpdatesManual { get; init; }
    public FrozenRankMappingDocument FrozenMapping { get; init; } = FrozenRankMapping.Document();
    public TransitionEnvironmentIdentity Environment { get; init; } = new();
    public IReadOnlyList<RankTransitionPlayerObservation> Players { get; init; } = [];
    public IReadOnlyList<RankTransitionWomObservation> WomObservations { get; init; } = [];
    public IReadOnlyList<RankTransitionManualWomCheck> ManualWomChecks { get; init; } = [];
    public IReadOnlyList<DiscordRoleObservation> DiscordRoles { get; init; } = [];
    public IReadOnlyList<DiscordMemberObservation> DiscordMembers { get; init; } = [];
    /// <summary>
    /// Frozen canonical rank-to-Discord-role IDs used by the manual report.
    /// This is captured during plan from reviewed configuration; report and
    /// verify never reconstruct it from post-cutover live role names.
    /// </summary>
    public IReadOnlyDictionary<string, string> FrozenDiscordRankRoleIds { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<DiscordRankMembershipPlan> DiscordMemberRankChanges { get; init; } = [];
    public IReadOnlyList<PromotionDecision> PromotionDecisions { get; init; } = [];
    public IReadOnlyList<TransitionWarning> Warnings { get; init; } = [];
    public IReadOnlyList<RankTransitionOperation> Operations { get; init; } = [];
    public IReadOnlyDictionary<string, string> ConfigKeyGuidance { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string IntegritySha256 { get; set; } = "";
}

public sealed record TransitionEnvironmentIdentity(
    string DatabaseIdentity = "",
    int WiseOldManGroupId = 0,
    string DiscordGuildId = "")
{
    public bool IsBound => !string.IsNullOrWhiteSpace(DatabaseIdentity) || WiseOldManGroupId > 0 || !string.IsNullOrWhiteSpace(DiscordGuildId);

    public void ValidateAgainst(TransitionEnvironmentIdentity current)
    {
        if (!string.IsNullOrWhiteSpace(DatabaseIdentity) &&
            !string.Equals(DatabaseIdentity, current.DatabaseIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The current database/server identity does not match the reviewed manifest.");
        }
        if (WiseOldManGroupId > 0 && WiseOldManGroupId != current.WiseOldManGroupId)
        {
            throw new InvalidOperationException("The current Wise Old Man group ID does not match the reviewed manifest.");
        }
        if (!string.IsNullOrWhiteSpace(DiscordGuildId) &&
            !string.Equals(DiscordGuildId, current.DiscordGuildId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The current Discord guild ID does not match the reviewed manifest.");
        }
    }
}

public sealed record RankTransitionPlayerObservation(
    int PlayerId,
    string Username,
    string Status,
    bool IsActive,
    string DatabaseCurrentRankBefore,
    string DatabaseCurrentRankTarget,
    string DatabaseEligibleRankBefore,
    string DatabaseEligibleRankTarget,
    long? LatestSnapshotId,
    string? LatestSnapshotHash,
    DateTimeOffset? LatestSnapshotAtUtc,
    DateTimeOffset? LastSyncedAtUtc,
    string? EvaluatedEligibleRank,
    string? EvaluatedEligibleExplanation,
    bool Protected,
    string? ProtectionReason);

public sealed record RankTransitionWomObservation(
    string Username,
    string? RoleBefore,
    string? RoleTarget,
    bool FreshObservation,
    bool Protected,
    bool IgnoredMismatch,
    string? Notes);

public sealed record RankTransitionManualWomCheck(
    string OperationId,
    string Username,
    string? RoleBefore,
    string? RoleAfter,
    bool IsActive,
    string Reason);

public sealed record DiscordRoleObservation(
    string RoleId,
    string NameBefore,
    string? NameTarget,
    string Permissions,
    int Position,
    int MembershipCount,
    bool Protected,
    string? Notes,
    IReadOnlyList<string>? MemberIds = null,
    string? MembershipHash = null);

public sealed record PromotionDecision(
    int PlayerId,
    string Username,
    string CurrentRank,
    string EvaluatedEligibleRank,
    string? WomRole,
    bool IsActive,
    bool QueuedThroughApprovalFlow,
    string Reason,
    string? SupersedesCandidateId = null,
    bool ApprovedForCutover = false);

public sealed record TransitionWarning(string Code, string Subject, string Details, bool BlocksApply);

public sealed record RankTransitionOperation(
    string OperationId,
    TransitionOperationKind Kind,
    string TargetKey,
    string? PropertyName,
    string? Before,
    string? After,
    bool IsExternal,
    bool IsActive,
    bool Protected,
    bool RequiresReview,
    long? SnapshotId,
    string? SnapshotHash,
    DateTimeOffset? LastSyncedAtUtc,
    string Reason);

public sealed class RankTransitionJournal
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ManifestId { get; init; } = "";
    public string ManifestIntegritySha256 { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<RankTransitionOperationResult> Results { get; set; } = [];
}

public sealed record RankTransitionOperationResult(
    string OperationId,
    TransitionOperationStatus Status,
    DateTimeOffset ObservedAtUtc,
    string? ObservedBefore,
    string? ObservedAfter,
    string Details,
    int Attempt = 1,
    bool ExternalIntentOwned = false,
    int? RelatedEntityId = null);

public static class RankTransitionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string ComputeIntegrity(RankTransitionManifest manifest)
    {
        var node = JsonSerializer.SerializeToNode(manifest, Options)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize rank transition manifest.");
        node["integritySha256"] = "";
        // The reviewed manifest can be created on Linux and verified on
        // Windows (or the other way around).  JsonNode's indented output
        // follows the process/platform newline convention, so hash only the
        // canonical LF representation and leave the on-disk JSON untouched.
        var payload = node.ToJsonString(Options)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

public sealed class RankTransitionManifestStore
{
    public async Task SaveAsync(string path, RankTransitionManifest manifest, CancellationToken ct = default)
    {
        manifest.IntegritySha256 = RankTransitionJson.ComputeIntegrity(manifest);
        await AtomicWriteAsync(path, RankTransitionJson.Serialize(manifest), ct);
    }

    public async Task<RankTransitionManifest> LoadAndVerifyAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<RankTransitionManifest>(stream, RankTransitionJson.Options, ct)
            ?? throw new InvalidDataException("Manifest is empty or invalid.");
        if (string.IsNullOrWhiteSpace(manifest.IntegritySha256) ||
            !string.Equals(manifest.IntegritySha256, RankTransitionJson.ComputeIntegrity(manifest), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest integrity check failed; the reviewed file was edited or truncated.");
        }

        if (manifest.SchemaVersion != RankTransitionManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported rank transition manifest schema version '{manifest.SchemaVersion}'.");
        }

        if (!string.Equals(
                manifest.CutoverApprovalPolicyVersion,
                RankTransitionManifest.ApprovedCutoverPolicyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The manifest cutover approval policy is unsupported; create a fresh schema-v2 plan.");
        }

        if (!string.Equals(manifest.FrozenMappingVersion, FrozenRankMapping.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported frozen mapping version '{manifest.FrozenMappingVersion}'.");
        }
        ValidateFrozenMapping(manifest.FrozenMapping);

        return manifest;
    }

    internal static void ValidateFrozenMapping(FrozenRankMappingDocument? mapping)
    {
        var expected = FrozenRankMapping.Document();
        if (mapping is null ||
            !string.Equals(mapping.Version, expected.Version, StringComparison.Ordinal) ||
            !SameMap(mapping.LegacyToCanonical, expected.LegacyToCanonical) ||
            !SameMap(mapping.DiscordRoleRenames, expected.DiscordRoleRenames))
        {
            throw new InvalidDataException("The manifest frozen mapping does not match the supported one-time transition mapping.");
        }
    }

    private static bool SameMap(
        IReadOnlyDictionary<string, string>? actual,
        IReadOnlyDictionary<string, string> expected)
    {
        if (actual is null || actual.Count != expected.Count) return false;
        return expected.All(pair => actual.TryGetValue(pair.Key, out var value) &&
                                    string.Equals(value, pair.Value, StringComparison.Ordinal));
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Manifest path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), ct);
                await writer.FlushAsync(ct);
                await stream.FlushAsync(ct);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}

public sealed class RankTransitionJournalStore
{
    public async Task<RankTransitionJournal> LoadOrCreateAsync(
        string path,
        RankTransitionManifest manifest,
        CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var journal = await JsonSerializer.DeserializeAsync<RankTransitionJournal>(stream, RankTransitionJson.Options, ct)
                ?? throw new InvalidDataException("Operation journal is empty or invalid.");
            if (journal.SchemaVersion != RankTransitionJournal.CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Unsupported rank transition journal schema version '{journal.SchemaVersion}'.");
            }
            if (!string.Equals(journal.ManifestId, manifest.ManifestId, StringComparison.Ordinal) ||
                !string.Equals(journal.ManifestIntegritySha256, manifest.IntegritySha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Operation journal does not belong to the verified manifest.");
            }
            return journal;
        }

        var created = new RankTransitionJournal
        {
            ManifestId = manifest.ManifestId,
            ManifestIntegritySha256 = manifest.IntegritySha256
        };
        await SaveAsync(path, created, ct);
        return created;
    }

    public async Task SaveAsync(string path, RankTransitionJournal journal, CancellationToken ct = default)
    {
        journal.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Journal path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(RankTransitionJson.Serialize(journal).AsMemory(), ct);
                await writer.FlushAsync(ct);
                await stream.FlushAsync(ct);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public static string PathFor(string manifestPath) => $"{manifestPath}.journal.json";
}
