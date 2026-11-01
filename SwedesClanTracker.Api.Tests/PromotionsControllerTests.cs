using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SwedesClanTracker.Api.Controllers;
using SwedesClanTracker.Core;

namespace SwedesClanTracker.Api.Tests;

public sealed class PromotionsControllerTests
{
    [Fact]
    public async Task Get_returns_persisted_candidate_classification_without_live_role_enrichment()
    {
        await using var db = CreateDbContext();
        var player = new Player
        {
            Username = "Gynofobi",
            CurrentRank = "Brassican",
            EligibleRank = "Commander",
            Status = PlayerStatus.ACTIVE,
            LastSeen = DateTimeOffset.UtcNow
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.PromotionCandidates.Add(new PromotionCandidate
        {
            PlayerId = player.Id,
            OldRank = "Brassican",
            NewRank = "Commander",
            Reason = "Commander via: Total level >= 2200",
            CandidateType = PromotionCandidateType.wom_already_at_new_rank,
            WomRoleAtEvaluation = "Commander",
            Status = PromotionStatus.PENDING,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = new PromotionsController(db, new ThrowingPromotionApprovalService());
        var response = Assert.IsType<OkObjectResult>(await controller.Get(CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            response.Value,
            new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() }
            }));
        var row = Assert.Single(json.RootElement.EnumerateArray());

        Assert.Equal("wom_already_at_new_rank", row.GetProperty("CandidateType").GetString());
        Assert.Equal("Commander", row.GetProperty("WomRoleAtEvaluation").GetString());
    }

    private static TrackerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TrackerDbContext(options);
    }

    private sealed class ThrowingPromotionApprovalService : IPromotionApprovalService
    {
        public Task<PromotionApprovalResult> ApproveAsync(
            PromotionCandidate candidate,
            Player player,
            string handledBy,
            string source,
            CancellationToken ct) =>
            throw new InvalidOperationException("GET must not call the approval service.");
    }
}
