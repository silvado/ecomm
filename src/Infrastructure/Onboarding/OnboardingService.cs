using Ecommerce.Application.Onboarding;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Onboarding;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Payments;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Infrastructure.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Onboarding;

/// <summary>
/// Primeiros passos (RF05): a situação de cada passo vem das próprias configurações da loja (salvar pela metade é salvar a
/// tela daquele assunto); só os passos pulados ficam numa tabela. Publicar muda a loja de "em implantação" para ativa no
/// banco <c>plataforma</c>.
/// </summary>
public sealed class OnboardingService(
    TenantDbContext db, PlatformDbContext platform, PaymentGatewayResolver payments, IOptions<PaymentOptions> paymentOptions,
    PlanDirectory plans, ITenantCatalog catalog, IMemoryCache cache, TimeProvider clock) : IOnboardingService
{
    public async Task<OnboardingView> GetAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var status = await platform.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Status).SingleAsync(ct);
        return await ViewAsync(tenantId, status, ct);
    }

    public async Task<OnboardingView> SetSkippedAsync(OnboardingStep onboardingStep, bool skipped, CancellationToken ct = default)
    {
        var step = onboardingStep;
        var tenantId = db.RequireTenantId();
        if (!Enum.IsDefined(step) || !OnboardingRules.CanSkip(step))
            throw new ArgumentException("Este passo é obrigatório e não pode ser pulado.", nameof(onboardingStep));

        if (skipped)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO onboarding_skips (tenant_id, step, skipped_at) VALUES ({tenantId}, {step.ToString()}, {clock.GetUtcNow()})
                ON CONFLICT (tenant_id, step) DO NOTHING
                """, ct);
        else
            await db.OnboardingSkips.Where(s => s.Step == step).ExecuteDeleteAsync(ct);
        return await GetAsync(ct);
    }

    public async Task<PublishResult> PublishAsync(CancellationToken ct = default)
    {
        var tenantId = db.RequireTenantId();
        var tenant = await platform.Tenants.SingleAsync(t => t.Id == tenantId, ct);
        var view = await ViewAsync(tenantId, tenant.Status, ct);

        if (tenant.Status is TenantStatus.Active or TenantStatus.ReadOnly) return new PublishResult(PublishOutcome.AlreadyPublished, view);
        if (tenant.Status is not TenantStatus.Onboarding) return new PublishResult(PublishOutcome.NotAllowed, view);
        if (!view.CanPublish) return new PublishResult(PublishOutcome.Incomplete, view);

        tenant.Publish();
        await platform.SaveChangesAsync(ct);
        // A vitrine e a validação de certificado leem a situação pelo catálogo em cache: vale na hora.
        catalog.Invalidate(tenantId);
        cache.Remove(StoreProfileService.PublicKey(tenantId));
        return new PublishResult(PublishOutcome.Published, await ViewAsync(tenantId, tenant.Status, ct));
    }

    private async Task<OnboardingView> ViewAsync(Guid tenantId, TenantStatus status, CancellationToken ct)
    {
        var plan = await plans.ForTenantAsync(tenantId, ct);
        var skipped = (await db.OnboardingSkips.AsNoTracking().Select(s => s.Step).ToListAsync(ct)).ToHashSet();
        var steps = OnboardingRules.Evaluate(await FactsAsync(tenantId, ct), plan.Features, skipped);
        return new OnboardingView(Tenant.IsStorefrontAvailable(status), steps, OnboardingRules.CanPublish(steps));
    }

    private async Task<OnboardingFacts> FactsAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await platform.Tenants.AsNoTracking().Where(t => t.Id == tenantId)
            .Select(t => new
            {
                Custom = t.CustomStorefrontSince != null,
                DomainVerified = t.Domains.Any(d => d.Kind != DomainKind.PlatformSubdomain && d.VerificationStatus == DomainVerificationStatus.Verified),
            }).SingleAsync(ct);
        var settings = await db.StoreSettings.AsNoTracking()
            .Select(s => new { s.OriginPostalCode, s.PickupEnabled, FiscalSaved = s.Fiscal != null }).SingleOrDefaultAsync(ct);
        // HIPÓTESE: empresa revisada = o Dono já salvou Dados da loja (nome, cores, textos ou logo); na loja personalizada, a
        // aparência é da equipe da plataforma.
        var companyReviewed = tenant.Custom || await db.StoreBrandings.AnyAsync(ct);
        var channels = (await db.ChannelConnections.AsNoTracking().Where(c => c.Status == ChannelConnectionStatus.Active)
            .Select(c => c.Channel).Distinct().ToListAsync(ct)).ToHashSet();

        return new OnboardingFacts(
            companyReviewed,
            tenant.DomainVerified,
            PaymentConfigured: await payments.ActiveAsync(ct) is not null,
            PaymentSimulated: paymentOptions.Value.UseFakeGateway,
            FiscalConfigured: settings?.FiscalSaved ?? false,
            ShippingConfigured: settings is { OriginPostalCode: not null } or { PickupEnabled: true },
            channels);
    }
}
