using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Ecommerce.Application.Storage;
using Ecommerce.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Ecommerce.Integration.Tests.Storage;

/// <summary>SeaweedFS real (mesma imagem do compose de dev), com credencial e os dois buckets do ADR-0005.</summary>
public sealed class SeaweedFixture : IAsyncLifetime
{
    public const string AccessKey = "teste";

    // Gerada por execução: nunca uma chave fixa no código.
    public string SecretKey { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private IContainer _container = null!;

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8333)}";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("chrislusf/seaweedfs:4.47")
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
            .WithEnvironment("S3_BUCKET", "fotos,documentos")
            .WithPortBinding(8333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("created bucket documentos"))
            .Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public S3FileStorage Storage(string? secretKey = null) => new(Options.Create(new StorageOptions
    {
        ServiceUrl = ServiceUrl,
        AccessKey = AccessKey,
        SecretKey = secretKey ?? SecretKey,
    }));
}

/// <summary>Contrato do adaptador S3 contra um serviço compatível real (não a AWS): o que o fake promete, o S3 cumpre.</summary>
public sealed class S3FileStorageTests(SeaweedFixture seaweed) : IClassFixture<SeaweedFixture>
{
    private static string NewKey() => $"{Guid.NewGuid()}/teste/{Guid.NewGuid()}";

    [Theory]
    [InlineData(StorageArea.Public)]
    [InlineData(StorageArea.Private)]
    public async Task Grava_le_e_apaga(StorageArea area)
    {
        using var storage = seaweed.Storage();
        var key = NewKey();
        var bytes = RandomNumberGenerator.GetBytes(64 * 1024);

        await storage.PutAsync(area, key, new MemoryStream(bytes), "image/png");
        var stored = await storage.GetAsync(area, key);
        Assert.NotNull(stored);
        Assert.Equal("image/png", stored.ContentType);
        Assert.Equal(bytes.Length, stored.Length);
        using (var copy = new MemoryStream())
        {
            await stored.Content.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        await stored.Content.DisposeAsync();

        await storage.DeleteAsync(area, key);
        Assert.Null(await storage.GetAsync(area, key));
    }

    [Fact]
    public async Task Inexistente_e_nulo_e_apagar_de_novo_nao_falha()
    {
        using var storage = seaweed.Storage();
        var key = NewKey();

        Assert.Null(await storage.GetAsync(StorageArea.Public, key));
        await storage.DeleteAsync(StorageArea.Public, key);
    }

    [Fact]
    public async Task Areas_sao_buckets_separados()
    {
        using var storage = seaweed.Storage();
        var key = NewKey();

        await storage.PutAsync(StorageArea.Private, key, new MemoryStream([1, 2, 3]), "application/xml");

        Assert.Null(await storage.GetAsync(StorageArea.Public, key));
    }

    [Fact]
    public async Task Credencial_errada_e_recusada()
    {
        using var storage = seaweed.Storage(secretKey: "outra-chave");

        await Assert.ThrowsAnyAsync<Amazon.S3.AmazonS3Exception>(() =>
            storage.PutAsync(StorageArea.Public, NewKey(), new MemoryStream([1]), "image/png"));
    }
}
