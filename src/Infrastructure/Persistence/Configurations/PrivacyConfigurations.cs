using Ecommerce.Domain.Privacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>RNF04: somente inclusão (a migração tira UPDATE/DELETE/TRUNCATE do papel da aplicação); sem FK para o pedido.</summary>
internal sealed class PersonalDataAccessConfiguration : IEntityTypeConfiguration<PersonalDataAccess>
{
    public void Configure(EntityTypeBuilder<PersonalDataAccess> builder)
    {
        builder.ToTable("personal_data_access_log");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.UserEmail).HasMaxLength(254).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(PersonalDataAccess.ActionMaxLength).IsRequired();
        builder.Property(a => a.Details).HasMaxLength(PersonalDataAccess.DetailsMaxLength);
        builder.Property(a => a.Ip).HasMaxLength(45);
        builder.HasIndex(a => new { a.TenantId, a.At });
        builder.HasIndex(a => new { a.TenantId, a.OrderId });
    }
}
