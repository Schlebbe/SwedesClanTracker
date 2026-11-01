using SwedesClanTracker.RankTransition;

namespace SwedesClanTracker.RankTransition.Tests;

public sealed class ManifestStoreTests
{
    [Fact]
    public async Task Integrity_is_cross_platform_and_accepts_lf_or_crlf_manifest_files()
    {
        const string expectedIntegrity = "64c79afb59c76dd7f245b2dba54cc00e7494fb343227aa770b0e74785ace965e";
        var directory = Path.Combine(Path.GetTempPath(), $"rank-transition-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var lfPath = Path.Combine(directory, "fixture-lf.json");
        var crlfPath = Path.Combine(directory, "fixture-crlf.json");

        try
        {
            var manifest = new RankTransitionManifest
            {
                ManifestId = "cross-platform-fixture",
                CreatedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
            };

            Assert.Equal(expectedIntegrity, RankTransitionJson.ComputeIntegrity(manifest));
            manifest.IntegritySha256 = expectedIntegrity;
            var json = RankTransitionJson.Serialize(manifest);
            var lfJson = json.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
            var crlfJson = lfJson.Replace("\n", "\r\n", StringComparison.Ordinal);
            var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(lfPath, lfJson, utf8);
            await File.WriteAllTextAsync(crlfPath, crlfJson, utf8);

            var store = new RankTransitionManifestStore();
            var lfLoaded = await store.LoadAndVerifyAsync(lfPath);
            var crlfLoaded = await store.LoadAndVerifyAsync(crlfPath);

            Assert.Equal(expectedIntegrity, lfLoaded.IntegritySha256);
            Assert.Equal(expectedIntegrity, crlfLoaded.IntegritySha256);
            Assert.Equal(lfLoaded.ManifestId, crlfLoaded.ManifestId);
            Assert.Equal(lfLoaded.CreatedAtUtc, crlfLoaded.CreatedAtUtc);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_and_load_round_trip_preserves_frozen_values_and_rejects_tampering()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rank-transition-{Guid.NewGuid():N}.json");
        try
        {
            var manifest = new RankTransitionManifest
            {
                ManifestId = "manifest-1",
                Operations =
                [
                    new RankTransitionOperation(
                        "op-1",
                        TransitionOperationKind.WiseOldManRole,
                        "Name",
                        "Role",
                        "General",
                        "Colonel",
                        true,
                        true,
                        false,
                        true,
                        null,
                        null,
                        null,
                        "frozen")
                ],
                ConfigKeyGuidance = new Dictionary<string, string> { ["DiscordBot:Token"] = "secret never persisted" }
            };
            var store = new RankTransitionManifestStore();
            await store.SaveAsync(path, manifest);
            var loaded = await store.LoadAndVerifyAsync(path);
            Assert.Equal("Colonel", loaded.Operations.Single().After);
            Assert.DoesNotContain("secret-value", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

            var text = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, text.Replace("Colonel", "Brigadier", StringComparison.Ordinal));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAndVerifyAsync(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
