using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Notify;

internal sealed class ChatGptKeys : IDisposable
{
    private readonly RSA rsa = RSA.Create(3072);
    public RsaSecurityKey Signing { get; }
    public SymmetricSecurityKey Encryption { get; }

    public ChatGptKeys(string directory)
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
