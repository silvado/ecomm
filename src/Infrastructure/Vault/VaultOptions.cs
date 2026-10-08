namespace Ecommerce.Infrastructure.Vault;

/// <summary>
/// Chaves mestras (KEK) do cofre, só por variável de ambiente (RNF05): <c>Vault__MasterKeys__v1=&lt;base64 de 32 bytes&gt;</c>
/// e <c>Vault__CurrentMasterKeyVersion=v1</c>. Para rotacionar: acrescentar v2, apontar a versão atual para v2,
/// rodar a recifragem e só então remover v1.
/// </summary>
public sealed class VaultOptions
{
    public const string Section = "Vault";

    public Dictionary<string, string> MasterKeys { get; set; } = [];
    public string CurrentMasterKeyVersion { get; set; } = string.Empty;

    internal byte[] MasterKey(string version)
    {
        if (!MasterKeys.TryGetValue(version, out var encoded))
            throw new InvalidOperationException($"Chave mestra '{version}' do cofre não configurada.");
        var key = Convert.FromBase64String(encoded);
        if (key.Length != EnvelopeCrypto.KeySize)
            throw new InvalidOperationException($"Chave mestra '{version}' precisa ter {EnvelopeCrypto.KeySize} bytes.");
        return key;
    }

    internal (string Version, byte[] Key) CurrentMasterKey()
    {
        if (string.IsNullOrWhiteSpace(CurrentMasterKeyVersion))
            throw new InvalidOperationException("Vault:CurrentMasterKeyVersion não configurado.");
        return (CurrentMasterKeyVersion, MasterKey(CurrentMasterKeyVersion));
    }
}
