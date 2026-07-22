using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Domain.Signaling;
using SonicRelay.Domain.Users;

namespace SonicRelay.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();
    public DbSet<SessionParticipant> SessionParticipants => Set<SessionParticipant>();
    public DbSet<SignalingEvent> SignalingEvents => Set<SignalingEvent>();
    public DbSet<DeviceIdentity> DeviceIdentities => Set<DeviceIdentity>();
    public DbSet<PairingChallenge> PairingChallenges => Set<PairingChallenge>();
    public DbSet<DevicePairing> DevicePairings => Set<DevicePairing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.ToTable("application_users");
            entity.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
        });

        modelBuilder.Entity<IdentityRole<Guid>>().ToTable("identity_roles");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("identity_user_claims");
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("identity_user_roles");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("identity_user_logins");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("identity_role_claims");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("identity_user_tokens");

        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("devices");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Platform).HasMaxLength(40).IsRequired();
            entity.HasIndex(x => x.OwnerUserId).HasDatabaseName("ix_devices_owner_user_id");
            entity.HasIndex(x => new { x.OwnerUserId, x.Type }).HasDatabaseName("ix_devices_owner_type");
        });

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
    }
}
