using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonicRelay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrateSessionOwnershipToDeviceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stream_sessions_owner_status",
                table: "stream_sessions");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "stream_sessions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "session_participants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "stream_sessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "session_participants",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_stream_sessions_owner_status",
                table: "stream_sessions",
                columns: new[] { "OwnerUserId", "Status" });
        }
    }
}
