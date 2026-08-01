using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonicRelay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DeviceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Platform = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CredentialSecretHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CredentialVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_identities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "device_pairings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublisherDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ViewerDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_pairings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pairing_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublisherDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pairing_challenges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_identities_status",
                table: "device_identities",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ix_device_pairings_publisher_device_id",
                table: "device_pairings",
                column: "PublisherDeviceId");

            migrationBuilder.CreateIndex(
                name: "ix_device_pairings_viewer_device_id",
                table: "device_pairings",
                column: "ViewerDeviceId");

            migrationBuilder.CreateIndex(
                name: "ix_pairing_challenges_expires_at",
                table: "pairing_challenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "ix_pairing_challenges_publisher_device_id",
                table: "pairing_challenges",
                column: "PublisherDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_identities");

            migrationBuilder.DropTable(
                name: "device_pairings");

            migrationBuilder.DropTable(
                name: "pairing_challenges");
        }
    }
}
