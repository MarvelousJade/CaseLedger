using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseLedger.Api.Data.Migrations.PostgreSQL
{
    /// <inheritdoc />
    public partial class AddAuditVerificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuditHeadHash",
                table: "Cases",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "0000000000000000000000000000000000000000000000000000000000000000");

            migrationBuilder.AddColumn<int>(
                name: "AuditHeadSequence",
                table: "Cases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE "Cases" AS cases
                SET "AuditHeadSequence" = latest."Sequence",
                    "AuditHeadHash" = latest."Hash"
                FROM (
                    SELECT DISTINCT ON ("CaseId")
                        "CaseId",
                        "Sequence",
                        "Hash"
                    FROM "AuditEvents"
                    ORDER BY "CaseId", "Sequence" DESC
                ) AS latest
                WHERE cases."Id" = latest."CaseId";
                """);

            migrationBuilder.CreateTable(
                name: "AuditVerificationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TargetSequence = table.Column<int>(type: "integer", nullable: false),
                    TargetHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ResultId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Valid = table.Column<bool>(type: "boolean", nullable: true),
                    CheckedEvents = table.Column<int>(type: "integer", nullable: true),
                    BrokenAt = table.Column<int>(type: "integer", nullable: true),
                    ChainHead = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    SnapshotSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditVerificationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditVerificationJobs_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeadLetteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LockId = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditVerificationJobs_CaseId_RequestedAt",
                table: "AuditVerificationJobs",
                columns: new[] { "CaseId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditVerificationJobs_ResultId",
                table: "AuditVerificationJobs",
                column: "ResultId",
                unique: true,
                filter: "\"ResultId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditVerificationJobs_Status_RequestedAt",
                table: "AuditVerificationJobs",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PublishedAt_DeadLetteredAt_NextAttemptAt",
                table: "OutboxMessages",
                columns: new[] { "PublishedAt", "DeadLetteredAt", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditVerificationJobs");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "AuditHeadHash",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "AuditHeadSequence",
                table: "Cases");
        }
    }
}
