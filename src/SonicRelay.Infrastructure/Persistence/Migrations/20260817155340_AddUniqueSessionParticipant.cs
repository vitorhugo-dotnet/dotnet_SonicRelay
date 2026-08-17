using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonicRelay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueSessionParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows written before the invariant existed would make the index creation fail, so
            // collapse each (session, device, role) group onto the participant the rest of the
            // system already treats as canonical: the oldest one, which is what both the join
            // endpoint and the signaling endpoint resolve to. Id breaks ties so the choice is
            // deterministic when two duplicates share a JoinedAt.
            migrationBuilder.Sql("""
                DELETE FROM session_participants p
                USING (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "SessionId", "DeviceId", "Role"
                               ORDER BY "JoinedAt", "Id"
                           ) AS row_rank
                    FROM session_participants
                ) ranked
                WHERE p."Id" = ranked."Id" AND ranked.row_rank > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_session_participants_session_device_role",
                table: "session_participants",
                columns: new[] { "SessionId", "DeviceId", "Role" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_session_participants_session_device_role",
                table: "session_participants");
        }
    }
}
