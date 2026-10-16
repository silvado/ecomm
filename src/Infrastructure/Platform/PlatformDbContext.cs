using Ecommerce.Domain.Billing;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Banco <c>plataforma</c>: catálogo de tenants, domínios, planos e identidade dos usuários do painel.
/// Sem RLS — acessado apenas pela resolução de tenant, login e casos de uso de superadmin (ADR-0001).
/// Tabelas com tenant_id aqui (vínculos, sessões) são sempre filtradas pelo tenant explicitamente.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanLimit> PlanLimits => Set<PlanLimit>();
    public DbSet<PlanFeature> PlanFeatures => Set<PlanFeature>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ChannelAccount> ChannelAccounts => Set<ChannelAccount>();
    public DbSet<VehicleBrand> VehicleBrands => Set<VehicleBrand>();
    public DbSet<VehicleModel> VehicleModels => Set<VehicleModel>();
    public DbSet<VehicleVersion> VehicleVersions => Set<VehicleVersion>();
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();
    public DbSet<PlatformAdminRecoveryCode> PlatformAdminRecoveryCodes => Set<PlatformAdminRecoveryCode>();
    public DbSet<PlatformAdminSession> PlatformAdminSessions => Set<PlatformAdminSession>();
    public DbSet<PlatformAuditEntry> PlatformAuditLog => Set<PlatformAuditEntry>();
    public DbSet<TenantTheme> TenantThemes => Set<TenantTheme>();
    public DbSet<TenantPage> TenantPages => Set<TenantPage>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionInvoice> SubscriptionInvoices => Set<SubscriptionInvoice>();
    public DbSet<SubscriptionEvent> SubscriptionEvents => Set<SubscriptionEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Slug).HasMaxLength(40).IsRequired();
            b.HasIndex(t => t.Slug).IsUnique();
            b.Property(t => t.Cnpj).HasConversion(c => c.Value, v => Cnpj.Parse(v)).HasMaxLength(14).IsRequired();
            b.HasIndex(t => t.Cnpj).IsUnique();
            b.Property(t => t.LegalName).HasMaxLength(200).IsRequired();
            b.Property(t => t.TradeName).HasMaxLength(120).IsRequired();
            b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.DatabaseKey).HasMaxLength(60).IsRequired();
            b.Property(t => t.PlanCode).HasMaxLength(30).IsRequired().HasDefaultValue(Plan.Essential);
            b.Ignore(t => t.IsCustomStorefront);
            b.HasOne<Plan>().WithMany().HasForeignKey(t => t.PlanCode).HasPrincipalKey(p => p.Code).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(t => t.Domains).WithOne().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(t => t.Domains).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<TenantDomain>(b =>
        {
            b.HasKey(d => d.Id);
            // Id gerado na aplicação e domínio adicionado pela coleção do tenant: sem isto o EF faria UPDATE.
            b.Property(d => d.Id).ValueGeneratedNever();
            b.Property(d => d.Host).HasMaxLength(253).IsRequired();
            b.HasIndex(d => d.Host).IsUnique();
            b.Property(d => d.Kind).HasConversion<string>().HasMaxLength(30);
            b.Property(d => d.VerificationStatus).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.VerificationToken).HasMaxLength(64);
            b.Property(d => d.LastCheckMessage).HasMaxLength(300);
            b.Ignore(d => d.VerificationRecordName);
            b.Ignore(d => d.VerificationRecordValue);
            b.Ignore(d => d.IsCustom);
            // Verificação periódica (RF03 CA2) procura os pendentes.
            b.HasIndex(d => d.VerificationStatus).HasFilter("verification_status = 'Pending'");
        });

        modelBuilder.Entity<Plan>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Code).HasMaxLength(30).IsRequired();
            b.Property(p => p.Name).HasMaxLength(60).IsRequired();
            b.Property(p => p.MonthlyPrice).HasPrecision(12, 2);
            b.HasMany(p => p.Limits).WithOne().HasForeignKey(l => l.PlanCode).HasPrincipalKey(p => p.Code).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(p => p.Limits).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasData(
                new Plan(new Guid("0199c4a0-0000-7000-8000-000000000001"), Plan.Essential, "Essencial"),
                new Plan(new Guid("0199c4a0-0000-7000-8000-000000000002"), Plan.Professional, "Profissional"),
                new Plan(new Guid("0199c4a0-0000-7000-8000-000000000003"), Plan.Complete, "Completo"));
        });

        modelBuilder.Entity<PlanLimit>(b =>
        {
            b.HasKey(l => new { l.PlanCode, l.Key });
            b.Property(l => l.Key).HasMaxLength(40);
            // HIPÓTESE (requisitos → Em aberto, Q17 e Q41): usuários 2/5/10; peças ativas 500/3.000/sem limite; IA 50/300/1.000 por mês.
            b.HasData(
                new PlanLimit(Plan.Essential, PlanLimit.Users, 2),
                new PlanLimit(Plan.Professional, PlanLimit.Users, 5),
                new PlanLimit(Plan.Complete, PlanLimit.Users, 10),
                new PlanLimit(Plan.Essential, PlanLimit.ActiveParts, 500),
                new PlanLimit(Plan.Professional, PlanLimit.ActiveParts, 3000),
                new PlanLimit(Plan.Essential, PlanLimit.AiListingsPerMonth, 50),
                new PlanLimit(Plan.Professional, PlanLimit.AiListingsPerMonth, 300),
                new PlanLimit(Plan.Complete, PlanLimit.AiListingsPerMonth, 1000));
        });

        modelBuilder.Entity<PlanFeature>(b =>
        {
            b.HasKey(f => new { f.PlanCode, f.Feature });
            b.Property(f => f.PlanCode).HasMaxLength(30);
            b.Property(f => f.Feature).HasMaxLength(60);
            b.HasOne<Plan>().WithMany().HasForeignKey(f => f.PlanCode).HasPrincipalKey(p => p.Code).OnDelete(DeleteBehavior.Cascade);
            // HIPÓTESE (Q41): Essencial = ML + IA de cadastro; Profissional = + Instagram, domínio próprio e demais IAs; Completo = + OLX.
            string[] essential = [PlanFeature.MercadoLivre, PlanFeature.AiPartFromPhotos];
            string[] professional = [.. essential, PlanFeature.Meta, PlanFeature.CustomDomain, PlanFeature.AiBuyerAnswers, PlanFeature.AiPriceSuggestion,
                PlanFeature.AiDailySummary];
            string[] complete = [.. professional, PlanFeature.Olx];
            b.HasData([
                .. essential.Select(f => new PlanFeature(Plan.Essential, f)),
                .. professional.Select(f => new PlanFeature(Plan.Professional, f)),
                .. complete.Select(f => new PlanFeature(Plan.Complete, f)),
            ]);
        });

        modelBuilder.Entity<UserAccount>(b =>
        {
            b.HasKey(u => u.Id);
            b.Property(u => u.Email).HasMaxLength(254).IsRequired();
            b.HasIndex(u => u.Email).IsUnique();
            b.Property(u => u.PasswordHash).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<TenantMembership>(b =>
        {
            b.HasKey(m => new { m.TenantId, m.UserId });
            b.HasIndex(m => m.UserId);
            b.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            b.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<UserAccount>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // RF26: roteia avisos de canal (inbox) para a loja dona da conta externa.
        modelBuilder.Entity<ChannelAccount>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Channel).HasMaxLength(ChannelAccount.ChannelMaxLength).IsRequired();
            b.Property(a => a.ExternalAccountId).HasMaxLength(ChannelAccount.ExternalAccountMaxLength).IsRequired();
            b.HasIndex(a => new { a.Channel, a.ExternalAccountId }).IsUnique();
            b.HasIndex(a => a.TenantId);
            b.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        // Fonte da tabela de veículos (RF09 CA2); replicada para ref.* em cada banco de lojas.
        VehicleMapping.Configure(modelBuilder, schema: null);

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => t.FamilyId);
            b.HasIndex(t => new { t.UserId, t.TenantId });
            b.HasOne<UserAccount>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // Superadmin (ADR-0007): contas separadas das de loja.
        modelBuilder.Entity<PlatformAdmin>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Email).HasMaxLength(254).IsRequired();
            b.HasIndex(a => a.Email).IsUnique();
            b.Property(a => a.PasswordHash).HasMaxLength(200).IsRequired();
            b.Property(a => a.TotpKeyVersion).HasMaxLength(20);
            b.Ignore(a => a.HasMfa);
        });

        modelBuilder.Entity<PlatformAdminRecoveryCode>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasIndex(c => new { c.AdminId, c.CodeHash });
            b.HasOne<PlatformAdmin>().WithMany().HasForeignKey(c => c.AdminId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlatformAdminSession>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.TokenHash).IsUnique();
            b.HasIndex(s => s.AdminId);
            b.HasOne<PlatformAdmin>().WithMany().HasForeignKey(s => s.AdminId).OnDelete(DeleteBehavior.Cascade);
        });

        // Tema e páginas exclusivos da loja personalizada (RF40 CA1): editados só pelo superadmin, lidos pela vitrine.
        modelBuilder.Entity<TenantTheme>(b =>
        {
            b.HasKey(t => t.TenantId);
            b.Property(t => t.Css).HasMaxLength(TenantTheme.CssMaxLength).IsRequired();
            b.Property(t => t.HeaderHtml).HasMaxLength(TenantTheme.HtmlMaxLength).IsRequired();
            b.Property(t => t.FooterHtml).HasMaxLength(TenantTheme.HtmlMaxLength).IsRequired();
            b.Property(t => t.UpdatedBy).HasMaxLength(254).IsRequired();
            b.HasOne<Tenant>().WithOne().HasForeignKey<TenantTheme>(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantPage>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Slug).HasMaxLength(TenantPage.SlugMaxLength).IsRequired();
            b.HasIndex(p => new { p.TenantId, p.Slug }).IsUnique();
            b.Property(p => p.Title).HasMaxLength(TenantPage.TitleMaxLength).IsRequired();
            b.Property(p => p.Html).HasMaxLength(TenantTheme.HtmlMaxLength).IsRequired();
            b.Property(p => p.UpdatedBy).HasMaxLength(254).IsRequired();
            b.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        // Assinatura das lojas (RF38): começa na publicação; faturas e avisos do gateway da plataforma.
        modelBuilder.Entity<Subscription>(b =>
        {
            b.HasKey(s => s.TenantId);
            b.HasOne<Tenant>().WithOne().HasForeignKey<Subscription>(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubscriptionInvoice>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Id).ValueGeneratedNever();
            b.Property(i => i.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(i => i.PaidVia).HasConversion<string>().HasMaxLength(20);
            b.Property(i => i.Description).HasMaxLength(SubscriptionInvoice.DescriptionMaxLength).IsRequired();
            b.Property(i => i.Amount).HasPrecision(12, 2);
            b.Property(i => i.PaidAmount).HasPrecision(12, 2);
            b.Property(i => i.ChargeId).HasMaxLength(SubscriptionInvoice.ChargeIdMaxLength);
            b.Property(i => i.PaymentUrl).HasMaxLength(SubscriptionInvoice.PaymentUrlMaxLength);
            b.Property(i => i.Note).HasMaxLength(SubscriptionInvoice.NoteMaxLength);
            // Uma mensalidade por competência: o job pode rodar de novo (ou em paralelo) sem duplicar.
            b.HasIndex(i => new { i.TenantId, i.Period }).IsUnique().HasFilter("kind = 'Monthly'");
            b.HasIndex(i => new { i.TenantId, i.DueDate });
            b.HasIndex(i => i.ChargeId).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubscriptionEvent>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Gateway).HasMaxLength(SubscriptionEvent.GatewayMaxLength).IsRequired();
            b.Property(e => e.IdempotencyKey).HasMaxLength(SubscriptionEvent.KeyMaxLength).IsRequired();
            b.Property(e => e.ChargeId).HasMaxLength(SubscriptionInvoice.ChargeIdMaxLength).IsRequired();
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(e => new { e.Gateway, e.IdempotencyKey }).IsUnique();
            b.HasIndex(e => e.ReceivedAt).HasFilter("status = 'Received'");
        });

        // Somente inclusão: a migração tira UPDATE/DELETE/TRUNCATE do papel da aplicação. Sem FK: sobrevive à conta e à loja.
        modelBuilder.Entity<PlatformAuditEntry>(b =>
        {
            b.ToTable("platform_audit_log");
            b.HasKey(e => e.Id);
            b.Property(e => e.AdminEmail).HasMaxLength(254).IsRequired();
            b.Property(e => e.Action).HasMaxLength(PlatformAuditEntry.ActionMaxLength).IsRequired();
            b.Property(e => e.Details).HasMaxLength(PlatformAuditEntry.DetailsMaxLength);
            b.Property(e => e.Ip).HasMaxLength(45);
            b.Property(e => e.CorrelationId).HasMaxLength(100);
            b.HasIndex(e => e.At);
            b.HasIndex(e => new { e.TenantId, e.At });
        });
    }
}
