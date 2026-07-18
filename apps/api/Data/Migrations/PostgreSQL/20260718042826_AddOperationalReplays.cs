using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseLedger.Api.Data.Migrations.PostgreSQL
{
    /// <inheritdoc />
    public partial class AddOperationalReplays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperationalReplays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDeadLetteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplayedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PreviousAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    PreviousErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Reason = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalReplays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationalReplays_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalReplays_ActorId",
                table: "OperationalReplays",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalReplays_Kind_SourceId_SourceDeadLetteredAt",
                table: "OperationalReplays",
                columns: new[] { "Kind", "SourceId", "SourceDeadLetteredAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationalReplays_ReplayedAt",
                table: "OperationalReplays",
                column: "ReplayedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperationalReplays");
        }
    }
}
