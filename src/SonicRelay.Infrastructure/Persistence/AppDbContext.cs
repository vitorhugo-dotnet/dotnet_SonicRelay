using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Domain.Signaling;
using SonicRelay.Domain.Users;

namespace SonicRelay.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();
    public DbSet<SessionParticipant> SessionParticipants => Set<SessionParticipant>();
    public DbSet<SignalingEvent> SignalingEvents => Set<SignalingEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.ToTable("application_users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
        });

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
            entity.HasIndex(x => new { x.OwnerUserId, x.Status }).HasDatabaseName("ix_stream_sessions_owner_status");
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
    }
}
