using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SwedesClanTracker.Core;

#nullable disable

namespace SwedesClanTracker.Core.Migrations;

/// <inheritdoc />
[DbContext(typeof(TrackerDbContext))]
[Migration("20261028130000_AddPromotionCandidateWomClassification")]
public partial class AddPromotionCandidateWomClassification : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CandidateType",
            table: "PromotionCandidates",
            type: "integer",
            nullable: false,
            defaultValue: 2);

        migrationBuilder.AddColumn<string>(
            name: "WomRoleAtEvaluation",
            table: "PromotionCandidates",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CandidateType",
            table: "PromotionCandidates");

        migrationBuilder.DropColumn(
            name: "WomRoleAtEvaluation",
            table: "PromotionCandidates");
    }
}
