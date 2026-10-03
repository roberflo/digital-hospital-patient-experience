using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class ActivityAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorRole",
                table: "Activities",
                type: "text",
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<string>(
                name: "ActorSubject",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MessageId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_CreatedAt_Id",
                table: "Activities",
                columns: new[] { "TenantId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_CreatedAt_Id",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ActorRole",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ActorSubject",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "MessageId",
                table: "Activities");
        }
    }
}
