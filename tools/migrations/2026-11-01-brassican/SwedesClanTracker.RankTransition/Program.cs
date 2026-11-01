using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.RankTransition;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        var command = args[0].ToLowerInvariant();
        if (command is not ("plan" or "apply" or "verify" or "rollback" or "discord-report" or "member-report"))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintUsage();
            return 2;
        }

        try
        {
            var configuration = BuildConfiguration();
            var manifestPath = GetManifestPath(args, configuration, command);
            var store = new RankTransitionManifestStore();
            if (command == "member-report")
            {
                return await MemberReportAsync(store, manifestPath);
            }

            using var provider = (command == "discord-report"
                    ? BuildDiscordServices(configuration)
                    : BuildServices(configuration))
                .BuildServiceProvider();

            return command switch
            {
                "plan" => await PlanAsync(provider, configuration, store, manifestPath, args),
                "apply" => await ApplyAsync(provider, configuration, store, manifestPath, args, rollback: false),
                "rollback" => await ApplyAsync(provider, configuration, store, manifestPath, args, rollback: true),
                "verify" => await VerifyAsync(provider, store, manifestPath),
                "discord-report" => await DiscordReportAsync(provider, configuration, store, manifestPath, args),
                _ => 2
            };
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Manifest safety check failed: {ex.Message}");
            return 3;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Safety precondition failed: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Rank transition command failed ({ex.GetType().Name}).");
            return 4;
        }
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

    private static IServiceCollection BuildServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddTrackerCore(configuration);
        services.AddHttpClient<IDiscordRoleGateway, DiscordRoleGateway>();
        return services;
    }

    private static IServiceCollection BuildDiscordServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddHttpClient<IDiscordRoleGateway, DiscordRoleGateway>();
        return services;
    }

    private static async Task<int> PlanAsync(
        ServiceProvider provider,
        IConfiguration configuration,
        RankTransitionManifestStore store,
        string manifestPath,
        string[] args)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        await using var readOnly = await BeginReadOnlyTransactionAsync(db, CancellationToken.None);
        var environment = await TransitionEnvironmentIdentityReader.ReadAsync(db, scope.ServiceProvider.GetRequiredService<IConfiguration>());
        var identityOverrides = await LoadIdentityOverridesAsync(args, configuration);
        var planner = new RankTransitionPlanner(
            db,
            scope.ServiceProvider.GetRequiredService<IWiseOldManClient>(),
            args.Contains("--no-discord", StringComparer.OrdinalIgnoreCase)
                ? null
                : scope.ServiceProvider.GetRequiredService<IDiscordRoleGateway>(),
            environment: environment);
        var manifest = await planner.PlanAsync(new RankTransitionPlannerOptions(
            IncludeDiscord: !args.Contains("--no-discord", StringComparer.OrdinalIgnoreCase),
            IncludeWom: !args.Contains("--no-wom", StringComparer.OrdinalIgnoreCase),
            DiscordRankRoleIds: args.Contains("--no-discord", StringComparer.OrdinalIgnoreCase) ? null : ReadDiscordRoleIds(configuration),
            ApproveQualifyingPromotions: args.Contains("--approve-qualifying-promotions", StringComparer.OrdinalIgnoreCase),
            DiscordMemberIdentityOverrides: identityOverrides?.PlayerToDiscordUserId,
            DiscordMemberRanksManual: !args.Contains("--automatic-discord-members", StringComparer.OrdinalIgnoreCase),
            WomUpdatesManual: true));
        await store.SaveAsync(manifestPath, manifest);
        var memberReport = RankTransitionMemberReport.Build(manifest);
        var memberReportPath = await RankTransitionMemberReport.WriteCsvAsync(memberReport, manifestPath);
        var markdownReportPath = await RankTransitionMemberReport.WriteMarkdownAsync(memberReport, manifestPath);
        var discordMemberReport = DiscordMemberReviewReport.Build(
            manifest,
            manifest.DiscordMembers,
            null,
            manifest.FrozenDiscordRankRoleIds,
            identityOverrides?.PlayerToDiscordUserId);
        var discordMemberReportPath = await DiscordMemberReviewReport.WriteCsvAsync(discordMemberReport, manifestPath);
        var discordMemberMarkdownPath = await DiscordMemberReviewReport.WriteMarkdownAsync(discordMemberReport, manifestPath);
        Console.WriteLine($"Read-only plan written to {Path.GetFullPath(manifestPath)}");
        Console.WriteLine(memberReport.ToConsoleSummary());
        Console.WriteLine($"Manual member cutover report written to {Path.GetFullPath(memberReportPath)}");
        Console.WriteLine($"Markdown member cutover report written to {Path.GetFullPath(markdownReportPath)}");
        Console.WriteLine(discordMemberReport.ToConsoleSummary());
        Console.WriteLine($"Discord member review CSV written to {Path.GetFullPath(discordMemberReportPath)}");
        Console.WriteLine($"Discord member review Markdown written to {Path.GetFullPath(discordMemberMarkdownPath)}");
        Console.WriteLine($"Manifest {manifest.ManifestId}: {manifest.Players.Count} players, {manifest.Operations.Count} operations, {manifest.Warnings.Count} warnings.");
        Console.WriteLine("Review the manifest and complete the maintenance, backup, and manual game/config gates before apply.");
        return 0;
    }

    private static async Task<int> DiscordReportAsync(
        ServiceProvider provider,
        IConfiguration configuration,
        RankTransitionManifestStore store,
        string manifestPath,
        string[] args)
    {
        var manifest = await store.LoadAndVerifyAsync(manifestPath);
        if (string.IsNullOrWhiteSpace(manifest.Environment.DiscordGuildId))
        {
            throw new InvalidOperationException("Discord report requires a frozen Discord guild identity in the manifest.");
        }
        var gateway = provider.GetRequiredService<IDiscordRoleGateway>();
        var freshGuild = await gateway.GetGuildObservationAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Discord report requires a fresh guild GET; credentials or guild ID are unavailable.");
        if (!string.IsNullOrWhiteSpace(manifest.Environment.DiscordGuildId) &&
            !string.Equals(manifest.Environment.DiscordGuildId, freshGuild.GuildId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fresh Discord guild identity does not match the frozen manifest.");
        }

        var identityOverrides = await LoadIdentityOverridesAsync(args, configuration);
        var report = DiscordMemberReviewReport.Build(
            manifest,
            manifest.DiscordMembers,
            freshGuild,
            manifest.FrozenDiscordRankRoleIds,
            identityOverrides?.PlayerToDiscordUserId);
        var csvPath = await DiscordMemberReviewReport.WriteCsvAsync(report, manifestPath);
        var markdownPath = await DiscordMemberReviewReport.WriteMarkdownAsync(report, manifestPath);
        Console.WriteLine(report.ToConsoleSummary());
        Console.WriteLine($"Discord member review CSV written to {Path.GetFullPath(csvPath)}");
        Console.WriteLine($"Discord member review Markdown written to {Path.GetFullPath(markdownPath)}");
        if (!freshGuild.MembersFullyObserved)
        {
            Console.WriteLine("Warning: the fresh Discord member observation is partial; treat this report as manual review only.");
        }
        return 0;
    }

    private static async Task<int> MemberReportAsync(
        RankTransitionManifestStore store,
        string manifestPath)
    {
        var manifest = await store.LoadAndVerifyAsync(manifestPath);
        var report = RankTransitionMemberReport.Build(manifest);
        var csvPath = await RankTransitionMemberReport.WriteCsvAsync(report, manifestPath);
        var markdownPath = await RankTransitionMemberReport.WriteMarkdownAsync(report, manifestPath);
        Console.WriteLine(report.ToConsoleSummary());
        Console.WriteLine($"Member report CSV written to {Path.GetFullPath(csvPath)}");
        Console.WriteLine($"Member report Markdown written to {Path.GetFullPath(markdownPath)}");
        Console.WriteLine("Frozen manifest was not changed; no database, WOM, Discord, or network services were initialized.");
        return 0;
    }

    private static async Task<int> ApplyAsync(
        ServiceProvider provider,
        IConfiguration configuration,
        RankTransitionManifestStore store,
        string manifestPath,
        string[] args,
        bool rollback)
    {
        var manifest = await store.LoadAndVerifyAsync(manifestPath);
        var prerequisites = new ApplyPrerequisites(
            GetOption(args, "--reviewed-by"),
            args.Contains("--maintenance-confirmed", StringComparer.OrdinalIgnoreCase),
            args.Contains("--backup-confirmed", StringComparer.OrdinalIgnoreCase),
            args.Contains("--game-reconciled", StringComparer.OrdinalIgnoreCase),
            ExplicitApply: true);
        prerequisites.Validate(manifest);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var currentEnvironment = await TransitionEnvironmentIdentityReader.ReadAsync(db, configuration);
        if (manifest.Environment.IsBound) manifest.Environment.ValidateAgainst(currentEnvironment);
        var applier = new RankTransitionApplier(
            db,
            scope.ServiceProvider.GetRequiredService<IWiseOldManClient>(),
            scope.ServiceProvider.GetService<IDiscordRoleGateway>(),
            environment: currentEnvironment);
        var journalPath = RankTransitionJournalStore.PathFor(manifestPath);
        var report = rollback
            ? await applier.RollbackAsync(manifest, journalPath, prerequisites)
            : await applier.ApplyAsync(manifest, journalPath, prerequisites);

        Console.WriteLine($"{(rollback ? "Rollback" : "Apply")} {(report.Completed ? "completed" : "paused")}: {report.Results.Count} durable operation results.");
        if (!report.Completed && !string.IsNullOrWhiteSpace(report.Failure)) Console.Error.WriteLine(report.Failure);
        return report.Completed ? 0 : 5;
    }

    private static async Task<int> VerifyAsync(
        ServiceProvider provider,
        RankTransitionManifestStore store,
        string manifestPath)
    {
        var manifest = await store.LoadAndVerifyAsync(manifestPath);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        await using var readOnly = await BeginReadOnlyTransactionAsync(db, CancellationToken.None);
        var currentEnvironment = await TransitionEnvironmentIdentityReader.ReadAsync(db, scope.ServiceProvider.GetRequiredService<IConfiguration>());
        if (manifest.Environment.IsBound) manifest.Environment.ValidateAgainst(currentEnvironment);
        var verifier = new RankTransitionVerifier(
            db,
            scope.ServiceProvider.GetRequiredService<IWiseOldManClient>(),
            scope.ServiceProvider.GetService<IDiscordRoleGateway>());
        var report = await verifier.VerifyAsync(manifest, RankTransitionJournalStore.PathFor(manifestPath));
        var drifted = report.Items.Count(x => x.Status == TransitionOperationStatus.Drifted);
        Console.WriteLine($"Verification {(report.SafeToProceed ? "matches frozen state" : "requires review")}: {report.Items.Count} operations, {drifted} drifted.");
        foreach (var item in report.Items.Where(x => x.Status == TransitionOperationStatus.Drifted)) Console.WriteLine($"  {item.OperationId}: {item.Details}");
        return report.SafeToProceed ? 0 : 6;
    }

    private static async Task<IDbContextTransaction?> BeginReadOnlyTransactionAsync(TrackerDbContext db, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true) return null;
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        }
        return transaction;
    }

    private static string GetManifestPath(string[] args, IConfiguration configuration, string command)
    {
        var provided = GetOption(args, "--manifest");
        if (!string.IsNullOrWhiteSpace(provided)) return provided;
        if (command != "plan") throw new InvalidOperationException("--manifest is required for apply, verify, rollback, discord-report, and member-report.");
        var directory = configuration["RankTransition:ManifestDirectory"];
        if (string.IsNullOrWhiteSpace(directory)) directory = ".rank-transition";
        return Path.Combine(directory, $"rank-transition-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json");
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static async Task<DiscordIdentityOverrides?> LoadIdentityOverridesAsync(
        string[] args,
        IConfiguration configuration)
    {
        var path = GetOption(args, "--discord-identity-overrides") ??
                   configuration["RankTransition:DiscordIdentityOverridesPath"];
        return string.IsNullOrWhiteSpace(path)
            ? null
            : await DiscordIdentityOverrides.LoadAsync(path);
    }

    private static IReadOnlyDictionary<string, string> ReadDiscordRoleIds(IConfiguration configuration) =>
        configuration.AsEnumerable()
            .Where(x => x.Key.StartsWith("DiscordBot:RankRoleIds:", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(
                x => x.Key["DiscordBot:RankRoleIds:".Length..],
                x => x.Value!,
                StringComparer.OrdinalIgnoreCase);

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static void PrintUsage()
    {
        Console.WriteLine("SwedesClanTracker.RankTransition");
        Console.WriteLine("  plan [--manifest path] [--no-discord] [--no-wom] [--approve-qualifying-promotions] [--discord-identity-overrides path] [--automatic-discord-members]");
        Console.WriteLine("  Standard CLI plans set manual WOM reconciliation: fresh WOM GETs gate apply/rollback, and the tool never issues WOM role writes.");
        Console.WriteLine("  discord-report --manifest path [--discord-identity-overrides path]");
        Console.WriteLine("  member-report --manifest path");
        Console.WriteLine("  verify --manifest path");
        Console.WriteLine("  apply --manifest path --reviewed-by name --maintenance-confirmed --backup-confirmed --game-reconciled");
        Console.WriteLine("  rollback --manifest path --reviewed-by name --maintenance-confirmed --backup-confirmed --game-reconciled");
        Console.WriteLine();
        Console.WriteLine("Connection settings come from configuration/environment. Secrets are never printed or stored in manifests.");
        Console.WriteLine("The tool never starts, stops, deploys, or mass-aligns Discord members.");
    }
}
