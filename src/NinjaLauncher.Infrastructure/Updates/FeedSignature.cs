using System.Security.Cryptography;
using System.Text;

namespace NinjaLauncher.Infrastructure.Updates;

/// <summary>
/// Detached ECDSA P-256 / SHA-256 signatures (IEEE P1363, base64) over a Velopack release feed.
/// The signed message is <c>"ninja-feed-v1\n" + channel + "\n" + feedBytes</c>, binding each feed to its channel
/// (a valid Linux feed cannot be replayed to Windows clients). Only the public key ships with the app.
/// </summary>
public static class FeedSignature
{
    public const string Extension = ".sig";

    public static string Sign(byte[] feed, string channel, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        if (key.KeySize != 256) throw new CryptographicException("Feed signing key must be ECDSA P-256.");
        return Convert.ToBase64String(key.SignData(Message(feed, channel), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public static bool Verify(byte[] feed, string channel, string signatureBase64, string publicKeyPem)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key.KeySize == 256 && key.VerifyData(Message(feed, channel), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Returns (privatePem, publicPem) for a fresh P-256 key pair.</summary>
    public static (string PrivatePem, string PublicPem) GenerateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Channel from a feed file name: <c>releases.win-x64.json</c> → <c>win-x64</c>.</summary>
    public static string ChannelFromFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (!name.StartsWith("releases.", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal) || name.Length <= 14)
            throw new ArgumentException($"'{name}' is not a Velopack feed file (releases.<channel>.json).");
        return name["releases.".Length..^".json".Length];
    }

    private static byte[] Message(byte[] feed, string channel) => [.. Encoding.UTF8.GetBytes($"ninja-feed-v1\n{channel}\n"), .. feed];
}
