using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TestControllerGrpc.Identity;

namespace TestController.Persistence.Configurations;

public sealed class PipelineAssignmentConfiguration : IEntityTypeConfiguration<PipelineAssignment>
{
    public void Configure(EntityTypeBuilder<PipelineAssignment> builder)
    {
        builder.ToTable("PipelineAssignments");
        builder.HasKey(pa => new { pa.UserId, pa.PipelineId });
        builder.Property(pa => pa.UserId).HasMaxLength(36);
        builder.Property(pa => pa.PipelineId).HasMaxLength(36);
        builder.Property(pa => pa.AssignedUtc).IsRequired();
        builder.Property(pa => pa.AssignedByUserId).HasMaxLength(36).IsRequired();
    }
}
