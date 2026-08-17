using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Domain.Sessions;
using SonicRelay.Domain.Signaling;

namespace SonicRelay.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();
    public DbSet<SessionParticipant> SessionParticipants => Set<SessionParticipant>();
    public DbSet<SignalingEvent> SignalingEvents => Set<SignalingEvent>();
    public DbSet<DeviceIdentity> DeviceIdentities => Set<DeviceIdentity>();
    public DbSet<PairingChallenge> PairingChallenges => Set<PairingChallenge>();
    public DbSet<DevicePairing> DevicePairings => Set<DevicePairing>();
    public DbSet<RelayDeviceSettings> RelayDeviceSettings => Set<RelayDeviceSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<StreamSession>(entity =>
        {
            entity.ToTable("stream_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.SourceDeviceId, x.Status }).HasDatabaseName("ix_stream_sessions_source_device_status");
        });

        modelBuilder.Entity<SessionParticipant>(entity =>
        {
            entity.ToTable("session_participants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.SessionId, x.Role }).HasDatabaseName("ix_session_participants_session_role");
            // A device holds at most one participant row per role in a session. Rejoin after a
            // network loss is a read-then-insert, and without this two concurrent attempts from
            // the same device could each insert a row — consuming a viewer slot twice and
            // splitting signaling routing across two participant ids.
            entity.HasIndex(x => new { x.SessionId, x.DeviceId, x.Role })
                .IsUnique()
                .HasDatabaseName("ux_session_participants_session_device_role");
        });

        modelBuilder.Entity<SignalingEvent>(entity =>
        {
            entity.ToTable("signaling_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.SessionId, x.CreatedAt }).HasDatabaseName("ix_signaling_events_session_created_at");
        });

        modelBuilder.Entity<DeviceIdentity>(entity =>
        {
            entity.ToTable("device_identities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.DeviceType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Platform).HasMaxLength(40).IsRequired();
            entity.Property(x => x.CredentialSecretHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => x.Status).HasDatabaseName("ix_device_identities_status");
        });

        modelBuilder.Entity<PairingChallenge>(entity =>
        {
            entity.ToTable("pairing_challenges");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.PublisherDeviceId).HasDatabaseName("ix_pairing_challenges_publisher_device_id");
            entity.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_pairing_challenges_expires_at");
        });

        modelBuilder.Entity<DevicePairing>(entity =>
        {
            entity.ToTable("device_pairings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => x.PublisherDeviceId).HasDatabaseName("ix_device_pairings_publisher_device_id");
            entity.HasIndex(x => x.ViewerDeviceId).HasDatabaseName("ix_device_pairings_viewer_device_id");
        });

        modelBuilder.Entity<RelayDeviceSettings>(entity =>
        {
            entity.ToTable("relay_device_settings");
            entity.HasKey(x => x.DeviceId);
            entity.Property(x => x.RelayMode).HasMaxLength(20).IsRequired();
            entity.Property(x => x.TurnUsername).HasMaxLength(256);
            entity.Property(x => x.TurnCredential).HasMaxLength(256);
        });
    }
}
