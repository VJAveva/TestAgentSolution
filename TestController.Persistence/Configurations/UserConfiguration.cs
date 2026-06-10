using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TestControllerGrpc.Identity;

namespace TestController.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(u => u.UserId);
        builder.Property(u => u.UserId).HasMaxLength(36);
        builder.Property(u => u.Username).HasMaxLength(64).IsRequired();
        builder.HasIndex(u => u.Username).IsUnique();
        builder.Property(u => u.Email).HasMaxLength(256).IsRequired();
        builder.Property(u => u.Role).HasConversion<int>();
        builder.Property(u => u.PasswordHash).IsRequired();
        builder.Property(u => u.CreatedUtc).IsRequired();
        builder.Property(u => u.CreatedByUserId).HasMaxLength(36);
    }
}
