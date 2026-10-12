using Ecommerce.Domain.Customers;
using Ecommerce.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Links de acesso à área do cliente (RF16): procurados pelo hash; contados por e-mail para o limite de pedidos de link.</summary>
internal sealed class CustomerAccessLinkConfiguration : IEntityTypeConfiguration<CustomerAccessLink>
{
    public void Configure(EntityTypeBuilder<CustomerAccessLink> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Email).HasMaxLength(StoreEmailRules.AddressMaxLength).IsRequired();
        builder.Property(l => l.TokenHash).HasMaxLength(32).IsRequired();
        builder.HasIndex(l => new { l.TenantId, l.TokenHash }).IsUnique();
        builder.HasIndex(l => new { l.TenantId, l.Email, l.CreatedAt });
    }
}

/// <summary>Sessões do comprador (RF16): procuradas pelo hash do cookie.</summary>
internal sealed class CustomerSessionConfiguration : IEntityTypeConfiguration<CustomerSession>
{
    public void Configure(EntityTypeBuilder<CustomerSession> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Email).HasMaxLength(StoreEmailRules.AddressMaxLength).IsRequired();
        builder.Property(s => s.TokenHash).HasMaxLength(32).IsRequired();
        builder.HasIndex(s => new { s.TenantId, s.TokenHash }).IsUnique();
        builder.HasIndex(s => new { s.TenantId, s.ExpiresAt });
    }
}
