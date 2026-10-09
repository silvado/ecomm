using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Ecommerce.Application.Storage;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Storage;

/// <summary>
/// Configuração do storage (ADR-0005). Credenciais só por variável de ambiente (RNF05):
/// <c>Storage__AccessKey</c>, <c>Storage__SecretKey</c>. <see cref="Provider"/> = <c>memory</c> usa o fake (testes).
/// </summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string Provider { get; set; } = "s3";
    public string ServiceUrl { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string PublicBucket { get; set; } = "fotos";
    public string PrivateBucket { get; set; } = "documentos";

    internal string Bucket(StorageArea area) => area == StorageArea.Public ? PublicBucket : PrivateBucket;
}

/// <summary>
/// Adaptador S3 (AWS SDK for .NET v4), apontável para qualquer serviço compatível (SeaweedFS em dev, provedor
/// gerenciado em produção). Endereçamento por caminho: com o endereço virtual o SDK procuraria <c>fotos.s3</c> na rede
/// do compose. Os checksums CRC que o SDK envia por padrão são aceitos pelo SeaweedFS 4.47 (S3FileStorageTests);
/// conferir de novo ao escolher o provedor de produção.
/// Fontes: https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TS3Config.html ;
/// https://docs.aws.amazon.com/sdkref/latest/guide/feature-dataintegrity.html
/// </summary>
public sealed class S3FileStorage : IFileStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly StorageOptions _options;

    public S3FileStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.ServiceUrl) || string.IsNullOrWhiteSpace(_options.AccessKey) || string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new InvalidOperationException("Storage: ServiceUrl, AccessKey e SecretKey precisam estar configurados.");

        _client = new AmazonS3Client(new BasicAWSCredentials(_options.AccessKey, _options.SecretKey), new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            AuthenticationRegion = _options.Region,
            ForcePathStyle = true,
        });
    }

    public async Task PutAsync(StorageArea area, string key, Stream content, string contentType, CancellationToken ct = default) =>
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket(area),
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, ct);

    public async Task<StoredFile?> GetAsync(StorageArea area, string key, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetObjectAsync(_options.Bucket(area), key, ct);
            return new StoredFile(response.ResponseStream, response.Headers.ContentType, response.ContentLength);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(StorageArea area, string key, CancellationToken ct = default) =>
        await _client.DeleteObjectAsync(_options.Bucket(area), key, ct);

    public void Dispose() => _client.Dispose();
}
