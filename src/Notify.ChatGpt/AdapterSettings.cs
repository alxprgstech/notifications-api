using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Notify.ChatGpt;

internal sealed record AdapterSettings(
    Uri PublicUrl, Uri SsoIssuer, string SsoClientId, string SsoClientSecret, string OwnerSub,
    string ClientId, string ClientSecret, Uri ClientRedirect, Uri NotifyUrl, string NotifyToken, string NotifySourceId,
    Uri SileroUrl, string DataPath, IPAddress? TrustedProxy, bool LocalHttp)
{
    public string Resource => new Uri(PublicUrl, "/mcp").AbsoluteUri;
    public string Metadata => new Uri(PublicUrl, "/.well-known/oauth-protected-resource/mcp").AbsoluteUri;
    public string Database => new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataPath, "adapter.db") }.ToString();

    public static AdapterSettings Read(IConfiguration config, bool development)
    {
        var section = config.GetSection("ChatGpt");
        string Required(string name) => section[name] is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"ChatGpt:{name} is required.");
        Uri Address(string name, bool privateNetwork = false)
        {
            if (!Uri.TryCreate(Required(name), UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && (privateNetwork || (development && uri.IsLoopback)))) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException($"Invalid ChatGpt:{name}.");
            return uri;
        }
        var publicUrl = Address("PublicUrl");
        var ssoIssuer = Address("SsoIssuer");
        var notify = Address("NotifyUrl", privateNetwork: true);
        var silero = Address("SileroUrl", privateNetwork: true);
        if (new[] { publicUrl, ssoIssuer, notify, silero }.Any(url => url.AbsolutePath != "/"))
            throw new InvalidOperationException("Adapter service URLs must be origins.");
        var callback = Address("ClientRedirect");
        if (!(development && callback.IsLoopback) && (callback.Host != "chatgpt.com" || !callback.IsDefaultPort ||
            !(callback.AbsolutePath == "/connector_platform_oauth_redirect" ||
            callback.AbsolutePath.StartsWith("/connector/oauth/", StringComparison.Ordinal))))
            throw new InvalidOperationException("ChatGpt:ClientRedirect must be the exact ChatGPT callback.");
        var sourceId = Required("NotifySourceId");
        if (sourceId.Length != 32 || !sourceId.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Invalid ChatGpt:NotifySourceId.");
        return new(publicUrl, ssoIssuer, Required("SsoClientId"), Required("SsoClientSecret"), Required("OwnerSub"),
            Required("ClientId"), Required("ClientSecret"), callback, notify, Required("NotifyToken"), sourceId,
            silero, Path.GetFullPath(Required("DataPath")),
            section["TrustedProxy"] is { Length: > 0 } proxy ? IPAddress.Parse(proxy) : null,
            publicUrl.Scheme == "http");
    }
}

internal sealed class AdapterDb(DbContextOptions<AdapterDb> options) : DbContext(options)
{
    public DbSet<VoiceAttempt> VoiceAttempts => Set<VoiceAttempt>();
}

[PrimaryKey(nameof(Key))]
internal sealed class VoiceAttempt
{
    public required string Key { get; init; }
    public required string Fingerprint { get; init; }
    public string? UploadId { get; set; }
}

internal sealed class AdapterKeys : IDisposable
{
    private readonly RSA rsa = RSA.Create(3072);
    public RsaSecurityKey Signing { get; }
    public SymmetricSecurityKey Encryption { get; }

    public AdapterKeys(string directory)
    {
        rsa.ImportFromPem(System.Text.Encoding.ASCII.GetString(
            LoadOrCreate(Path.Combine(directory, "signing.pem"), () => System.Text.Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem()))));
        Signing = new RsaSecurityKey(rsa) { KeyId = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())) };
        var key = LoadOrCreate(Path.Combine(directory, "encryption.key"), () => RandomNumberGenerator.GetBytes(32));
        Encryption = new SymmetricSecurityKey(key) { KeyId = Convert.ToHexString(SHA256.HashData(key)) };
    }

    private static byte[] LoadOrCreate(string path, Func<byte[]> create)
    {
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var data = create();
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options))
            {
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { return File.ReadAllBytes(path); }
            return data;
        }
        finally { File.Delete(temp); }
    }

    public void Dispose() => rsa.Dispose();
}
