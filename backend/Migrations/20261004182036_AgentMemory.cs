using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgentMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_TenantId_ConversationId",
                table: "Messages");

            migrationBuilder.CreateTable(
                name: "ContactMemories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactMemories", x => x.Id);
                    table.UniqueConstraint("AK_ContactMemories_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContactMemories_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContactMemories_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_ConversationId_CreatedAt",
                table: "Messages",
                columns: new[] { "TenantId", "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_ContactId_CreatedAt",
                table: "Activities",
                columns: new[] { "TenantId", "ContactId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_ConversationId_CreatedAt",
                table: "Activities",
                columns: new[] { "TenantId", "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactMemories_TenantId_ContactId_Key",
                table: "ContactMemories",
                columns: new[] { "TenantId", "ContactId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactMemories");

            migrationBuilder.DropIndex(
                name: "IX_Messages_TenantId_ConversationId_CreatedAt",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_ContactId_CreatedAt",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_ConversationId_CreatedAt",
                table: "Activities");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_ConversationId",
                table: "Messages",
                columns: new[] { "TenantId", "ConversationId" });
        }
    }
}
