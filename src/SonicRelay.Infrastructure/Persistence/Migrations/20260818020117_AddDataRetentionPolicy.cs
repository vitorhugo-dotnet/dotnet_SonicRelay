using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonicRelay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDataRetentionPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "relay_device_settings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Existing rows have no recorded collection time. The column default (year 1) would
            // read as "older than any cutoff" and the first retention pass would wipe every
            // relay override in the database. UpdatedAt is the only evidence we have of when the
            // row was written, and it is never later than the real collection time, so backfill
            // from it: the row still expires on schedule, just never earlier than it should.
            migrationBuilder.Sql(
                @"UPDATE relay_device_settings SET ""CreatedAt"" = ""UpdatedAt"" WHERE ""CreatedAt"" = '0001-01-01T00:00:00Z';");

            migrationBuilder.CreateIndex(
                name: "ix_stream_sessions_created_at",
                table: "stream_sessions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_signaling_events_created_at",
                table: "signaling_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_session_participants_joined_at",
                table: "session_participants",
                column: "JoinedAt");

            migrationBuilder.CreateIndex(
                name: "ix_relay_device_settings_created_at",
                table: "relay_device_settings",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_pairing_challenges_created_at",
                table: "pairing_challenges",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_device_pairings_created_at",
                table: "device_pairings",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_device_identities_created_at",
                table: "device_identities",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stream_sessions_created_at",
                table: "stream_sessions");

            migrationBuilder.DropIndex(
                name: "ix_signaling_events_created_at",
                table: "signaling_events");

            migrationBuilder.DropIndex(
                name: "ix_session_participants_joined_at",
                table: "session_participants");

            migrationBuilder.DropIndex(
                name: "ix_relay_device_settings_created_at",
                table: "relay_device_settings");

            migrationBuilder.DropIndex(
                name: "ix_pairing_challenges_created_at",
                table: "pairing_challenges");

            migrationBuilder.DropIndex(
                name: "ix_device_pairings_created_at",
                table: "device_pairings");

            migrationBuilder.DropIndex(
                name: "ix_device_identities_created_at",
                table: "device_identities");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "relay_device_settings");
        }
    }
}
