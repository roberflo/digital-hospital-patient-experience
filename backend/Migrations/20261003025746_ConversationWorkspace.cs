using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConversationWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "Opportunities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHistory",
                table: "Messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReceivedAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Labels",
                table: "Conversations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LastMessage",
                table: "Conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "Conversations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SnoozedUntil",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "Conversations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleStage",
                table: "Contacts",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastWebhookAt",
                table: "Channels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConversationReads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    LastReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationReads", x => x.Id);
                    table.UniqueConstraint("AK_ConversationReads_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ConversationReads_Conversations_TenantId_ConversationId",
                        columns: x => new { x.TenantId, x.ConversationId },
                        principalTable: "Conversations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationReads_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SavedReplies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedReplies", x => x.Id);
                    table.UniqueConstraint("AK_SavedReplies_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SavedReplies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_ConversationId",
                table: "Opportunities",
                columns: new[] { "TenantId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_CompanyId",
                table: "Contacts",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReads_TenantId_ConversationId_Subject",
                table: "ConversationReads",
                columns: new[] { "TenantId", "ConversationId", "Subject" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Contacts_Companies_TenantId_CompanyId",
                table: "Contacts",
                columns: new[] { "TenantId", "CompanyId" },
                principalTable: "Companies",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Opportunities_Conversations_TenantId_ConversationId",
                table: "Opportunities",
                columns: new[] { "TenantId", "ConversationId" },
                principalTable: "Conversations",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("UPDATE \"Messages\" SET \"ReceivedAt\"=\"CreatedAt\";");
            migrationBuilder.Sql("UPDATE \"Conversations\" SET \"State\"=CASE WHEN \"Status\"='closed' THEN 'resolved' ELSE 'open' END, \"Priority\"='normal';");
            migrationBuilder.Sql("UPDATE \"Contacts\" SET \"LifecycleStage\"='lead';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contacts_Companies_TenantId_CompanyId",
                table: "Contacts");

            migrationBuilder.DropForeignKey(
                name: "FK_Opportunities_Conversations_TenantId_ConversationId",
                table: "Opportunities");

            migrationBuilder.DropTable(
                name: "ConversationReads");

            migrationBuilder.DropTable(
                name: "SavedReplies");

            migrationBuilder.DropIndex(
                name: "IX_Opportunities_TenantId_ConversationId",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_TenantId_CompanyId",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "IsHistory",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReceivedAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Labels",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LastMessage",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "SnoozedUntil",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "State",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "LifecycleStage",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "LastWebhookAt",
                table: "Channels");
        }
    }
}
