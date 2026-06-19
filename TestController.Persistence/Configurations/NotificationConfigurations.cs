using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TestControllerGrpc.Models;

namespace TestController.Persistence.Configurations;

public sealed class NotificationMuteConfiguration : IEntityTypeConfiguration<NotificationMute>
{
    public void Configure(EntityTypeBuilder<NotificationMute> builder)
    {
        builder.ToTable("NotificationMutes");
        builder.HasKey(m => m.MuteId);
        builder.Property(m => m.Target).HasMaxLength(256).IsRequired();
        builder.Property(m => m.TargetType).HasMaxLength(16).IsRequired();
        builder.Property(m => m.MutedByUserId).HasMaxLength(36).IsRequired();
        builder.HasIndex(m => new { m.Target, m.TargetType }).IsUnique();
    }
}

public sealed class NotificationCooldownConfiguration : IEntityTypeConfiguration<NotificationCooldown>
{
    public void Configure(EntityTypeBuilder<NotificationCooldown> builder)
    {
        builder.ToTable("NotificationCooldowns");
        builder.HasKey(c => c.CooldownId);
        builder.Property(c => c.Target).HasMaxLength(256).IsRequired();
        builder.Property(c => c.TargetType).HasMaxLength(16).IsRequired();
        builder.HasIndex(c => new { c.Target, c.TargetType }).IsUnique();
    }
}
