using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TestControllerGrpc.Identity;

namespace TestController.Persistence.Configurations;

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("Sessions");
        builder.HasKey(s => s.SessionId);
        builder.Property(s => s.SessionId).HasMaxLength(36);
        builder.Property(s => s.UserId).HasMaxLength(36);
        builder.Property(s => s.GuestId).HasMaxLength(36);
        builder.Property(s => s.TokenHash).HasMaxLength(32).IsRequired();
        builder.HasIndex(s => s.TokenHash).IsUnique()
            .HasFilter("[RevokedUtc] IS NULL");
        builder.Property(s => s.ClientKind).HasConversion<int>();
        builder.Property(s => s.IpAddress).HasMaxLength(45);
        builder.Property(s => s.CreatedUtc).IsRequired();
        builder.Property(s => s.LastUsedUtc).IsRequired();
    }
}
