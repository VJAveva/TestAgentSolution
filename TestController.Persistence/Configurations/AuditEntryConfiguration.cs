using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TestControllerGrpc.Authorization;

namespace TestController.Persistence.Configurations;

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntries");
        builder.HasKey(a => a.AuditId);
        builder.Property(a => a.AuditId).ValueGeneratedOnAdd();
        builder.Property(a => a.UserId).HasMaxLength(36);
        builder.Property(a => a.GuestId).HasMaxLength(36);
        builder.Property(a => a.RoleAtTime).HasConversion<int?>();
        builder.Property(a => a.ActionName).HasMaxLength(64).IsRequired();
        builder.Property(a => a.ResourceId).HasMaxLength(256);
        builder.Property(a => a.ReasonCode).HasMaxLength(64).IsRequired();
        builder.Property(a => a.TimestampUtc).IsRequired();
        builder.Property(a => a.ClientKind).HasConversion<int>();
        builder.Property(a => a.CorrelationId).HasMaxLength(64);

        builder.HasIndex(a => new { a.UserId, a.TimestampUtc })
            .IsDescending(false, true);
        builder.HasIndex(a => new { a.ActionName, a.TimestampUtc })
            .IsDescending(false, true);
        builder.HasIndex(a => a.TimestampUtc)
            .IsDescending(true);
    }
}
