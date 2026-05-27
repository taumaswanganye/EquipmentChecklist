using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EquipmentChecklist.Services;

/// <summary>
/// Issues and verifies short-lived offline-access JWTs for biometric / PIN login.
///
/// The voucher is a compact JWS (header.payload.signature) signed with the
/// server's RS256 key. The browser (and its Service Worker) caches the latest
/// voucher in IndexedDB, then verifies it locally using the server's public
/// key (fetched from /api/v1/auth-pubkey at enrollment time) so login works
/// fully offline up to <c>expires_at</c>.
///
/// PHASE 1: the service is wired up and the key is generated/loaded. The
/// SecurityController issues / verifies vouchers in phase 3.
/// </summary>
public class OfflineVoucherService
{
    /// <summary>Voucher lifetime when issued. 24 hours per user spec.</summary>
    public static readonly TimeSpan VoucherLifetime = TimeSpan.FromHours(24);

    private readonly RSA _key;
    private readonly string _kid;          // key id (thumbprint) — embedded in header/JWK
    private readonly object _signLock = new();

    public OfflineVoucherService(IWebHostEnvironment env, IConfiguration cfg)
    {
        // Key is stored under the content root (NOT wwwroot) so it isn't served
        // statically. Configurable via Auth:VoucherKeyPath.
        var keyPath = cfg["Auth:VoucherKeyPath"]
                      ?? Path.Combine(env.ContentRootPath, "App_Data", "voucher_rsa.pem");

        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

        _key = RSA.Create(2048);
        if (File.Exists(keyPath))
        {
            try { _key.ImportFromPem(File.ReadAllText(keyPath)); }
            catch
            {
                // Corrupt key file — regenerate and overwrite. Existing vouchers
                // become invalid; clients fall back to password login.
                _key.Dispose();
                _key = RSA.Create(2048);
                File.WriteAllText(keyPath, _key.ExportRSAPrivateKeyPem());
            }
        }
        else
        {
            File.WriteAllText(keyPath, _key.ExportRSAPrivateKeyPem());
        }

        // Key id = first 16 chars of the SHA-256 of the SPKI (public key info).
        var spki   = _key.ExportSubjectPublicKeyInfo();
        var digest = SHA256.HashData(spki);
        _kid       = Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }

    /// <summary>Returns the public key as a JWK (JSON Web Key) for clients to pin.</summary>
    public object GetPublicJwk()
    {
        var p = _key.ExportParameters(includePrivateParameters: false);
        return new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid = _kid,
            n   = Base64Url(p.Modulus  ?? Array.Empty<byte>()),
            e   = Base64Url(p.Exponent ?? Array.Empty<byte>())
        };
    }

    /// <summary>Issues a signed voucher for the given payload. RS256.</summary>
    public string IssueVoucher(VoucherPayload payload)
    {
        var header = new { alg = "RS256", typ = "JWT", kid = _kid };
        var headerJson  = JsonSerializer.Serialize(header,  JsonOpts);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);

        var headerB64  = Base64Url(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64Url(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{headerB64}.{payloadB64}";

        byte[] sig;
        lock (_signLock)
        {
            sig = _key.SignData(
                Encoding.UTF8.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        return $"{signingInput}.{Base64Url(sig)}";
    }

    /// <summary>Verify and decode a voucher. Returns null on any failure (signature, format, expiry).</summary>
    public VoucherPayload? VerifyVoucher(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;

            var signingInput = $"{parts[0]}.{parts[1]}";
            var sig          = Base64UrlDecode(parts[2]);

            var ok = _key.VerifyData(
                Encoding.UTF8.GetBytes(signingInput),
                sig,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            if (!ok) return null;

            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            var payload     = JsonSerializer.Deserialize<VoucherPayload>(payloadJson, JsonOpts);
            if (payload == null) return null;

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (payload.Exp <= nowUnix) return null; // expired
            return payload;
        }
        catch
        {
            return null;
        }
    }

    // ───── helpers ────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
               .TrimEnd('=')
               .Replace('+', '-')
               .Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}

/// <summary>
/// Voucher claims. Keep field names compact — they go over the wire on every
/// offline login. Uses snake_case via [JsonPropertyName] so JS can read directly.
/// </summary>
public class VoucherPayload
{
    [JsonPropertyName("sub")]       public string Sub        { get; set; } = "";  // userId
    [JsonPropertyName("name")]      public string Name       { get; set; } = "";  // full name
    [JsonPropertyName("email")]     public string Email      { get; set; } = "";
    [JsonPropertyName("roles")]     public string[] Roles    { get; set; } = Array.Empty<string>();
    [JsonPropertyName("device_id")] public int    DeviceId   { get; set; }        // UserCredential.Id
    [JsonPropertyName("pin_hash")]  public string? PinHash   { get; set; }        // PBKDF2 hash of fallback PIN
    [JsonPropertyName("iat")]       public long   Iat        { get; set; }        // issued-at unix seconds
    [JsonPropertyName("exp")]       public long   Exp        { get; set; }        // expires unix seconds
}
