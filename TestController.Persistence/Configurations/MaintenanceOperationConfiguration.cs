using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TestController.Persistence.Maintenance;

namespace TestController.Persistence.Configurations;

public sealed class MaintenanceOperationConfiguration : IEntityTypeConfiguration<MaintenanceOperationRecord>
{
    public void Configure(EntityTypeBuilder<MaintenanceOperationRecord> builder)
    {
        builder.ToTable("MaintenanceOperations");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasMaxLength(36);
        builder.Property(m => m.NodeId).HasMaxLength(128).IsRequired();
        builder.Property(m => m.Kind).HasConversion<int>();
        builder.Property(m => m.SnapshotName).HasMaxLength(256);
        builder.Property(m => m.ScriptPath).HasMaxLength(512);
        builder.Property(m => m.State).HasConversion<int>();
        builder.Property(m => m.Phase).HasConversion<int>();
        builder.Property(m => m.TriggerSource).HasConversion<int>();
        builder.Property(m => m.TriggeredBy).HasMaxLength(128);
        builder.Property(m => m.Reason).HasMaxLength(512);
        builder.Property(m => m.LinkedRunId).HasMaxLength(36);
        builder.Property(m => m.StartedUtc).IsRequired();
        builder.Property(m => m.FailurePhase).HasConversion<int?>();
        builder.Property(m => m.LogPath).HasMaxLength(512);

        builder.HasIndex(m => new { m.NodeId, m.StartedUtc }).IsDescending(false, true);
        builder.HasIndex(m => m.StartedUtc).IsDescending(true);
    }
}
