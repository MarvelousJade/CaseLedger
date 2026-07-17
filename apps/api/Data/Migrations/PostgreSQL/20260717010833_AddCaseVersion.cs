using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseLedger.Api.Data.Migrations.PostgreSQL
{
    /// <inheritdoc />
    public partial class AddCaseVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Cases"
                SET "Version" = gen_random_uuid()
                WHERE "Version" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "Version",
                table: "Cases",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "Cases");
        }
    }
}
