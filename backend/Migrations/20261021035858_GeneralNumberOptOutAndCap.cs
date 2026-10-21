using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class GeneralNumberOptOutAndCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OptOutWithdrawnAt",
                table: "AppointmentReminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GeneralOptOuts",
                columns: table => new
                {
                    PhoneHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneralOptOuts", x => x.PhoneHash);
                });

            migrationBuilder.CreateTable(
                name: "GeneralReplyCaps",
                columns: table => new
                {
                    PhoneHash = table.Column<string>(type: "text", nullable: false),
                    LastReplyAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneralReplyCaps", x => x.PhoneHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeneralReplyCaps_LastReplyAt",
                table: "GeneralReplyCaps",
                column: "LastReplyAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GeneralOptOuts");

            migrationBuilder.DropTable(
                name: "GeneralReplyCaps");

            migrationBuilder.DropColumn(
                name: "OptOutWithdrawnAt",
                table: "AppointmentReminders");
        }
    }
}
