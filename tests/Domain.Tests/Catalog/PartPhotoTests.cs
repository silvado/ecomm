using Ecommerce.Domain.Catalog;

namespace Ecommerce.Domain.Tests.Catalog;

public sealed class PartPhotoTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static Part NewPart() =>
        Part.Create(Guid.CreateVersion7(), "A-1", new PartDetails("Farol", null, PartCondition.Used, 100m, null, null, null, null, null), Now);

    [Fact]
    public void Fotos_entram_no_fim_e_a_primeira_e_a_capa()
    {
        var part = NewPart();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        part.AddPhoto(a, "image/jpeg", Now);
        part.AddPhoto(b, "image/png", Now);

        Assert.Equal([a, b], part.Photos.Select(p => p.Id));
        Assert.Equal([0, 1], part.Photos.Select(p => p.Position));
    }

    [Fact]
    public void No_maximo_20_fotos()
    {
        var part = NewPart();
        for (var i = 0; i < Part.MaxPhotos; i++) part.AddPhoto(Guid.NewGuid(), "image/jpeg", Now);

        Assert.Throws<InvalidOperationException>(() => part.AddPhoto(Guid.NewGuid(), "image/jpeg", Now));
    }

    [Fact]
    public void Remover_renumera_sem_buracos()
    {
        var part = NewPart();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        ids.ForEach(id => part.AddPhoto(id, "image/jpeg", Now));

        part.RemovePhoto(ids[0], Now);

        Assert.Equal([ids[1], ids[2]], part.Photos.Select(p => p.Id));
        Assert.Equal([0, 1], part.Photos.Select(p => p.Position));
    }

    [Fact]
    public void Reordenar_exige_exatamente_as_fotos_da_peca()
    {
        var part = NewPart();
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        part.AddPhoto(a, "image/jpeg", Now);
        part.AddPhoto(b, "image/jpeg", Now);
        part.AddPhoto(c, "image/jpeg", Now);

        part.ReorderPhotos([c, a, b], Now);
        Assert.Equal([c, a, b], part.Photos.Select(p => p.Id));

        Assert.Throws<ArgumentException>(() => part.ReorderPhotos([c, a], Now));                 // faltando
        Assert.Throws<ArgumentException>(() => part.ReorderPhotos([c, a, a], Now));              // repetida
        Assert.Throws<ArgumentException>(() => part.ReorderPhotos([c, a, Guid.NewGuid()], Now)); // de outra peça
        Assert.Equal([c, a, b], part.Photos.Select(p => p.Id));
    }

    [Fact]
    public void Ativar_exige_foto_e_peca_ativa_nao_perde_a_ultima()
    {
        var part = NewPart();
        Assert.Throws<InvalidOperationException>(() => part.Activate(Now));

        var only = Guid.NewGuid();
        part.AddPhoto(only, "image/jpeg", Now);
        part.Activate(Now);
        Assert.Throws<InvalidOperationException>(() => part.RemovePhoto(only, Now));

        part.Deactivate(Now);
        part.RemovePhoto(only, Now);
        Assert.Empty(part.Photos);
    }

    [Fact]
    public void Chaves_ficam_sob_o_tenant_e_a_peca()
    {
        var (tenant, part, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal($"{tenant}/pecas/{part}/{photo}-800.webp", PartPhoto.PublicKey(tenant, part, photo, 800));
        Assert.Equal($"{tenant}/pecas/{part}/{photo}-original", PartPhoto.OriginalKey(tenant, part, photo));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartPhoto.PublicKey(tenant, part, photo, 1024));
    }
}
