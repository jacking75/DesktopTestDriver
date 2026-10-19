using System.Security.Cryptography;
using System.Security.Principal;

namespace DesktopTestDriver;

public static class OwnerProvisioning
{
    public static OwnerKey Initialize(string directory, string keyId)
    {
        if (!Wire.Id(keyId)) throw new DriverException(Error.InvalidRequest);
        directory = Path.GetFullPath(directory); RunStore.CreatePrivateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var owner = new OwnerKey(WindowsIdentity.GetCurrent().Name,keyId,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        File.WriteAllText(Path.Combine(directory,"owner-private.pem"),key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(directory,"owners.json"),Wire.Encode(new[] { owner }));
        return owner;
    }
    public static void Authorize(string configPath, string keyPath, string output)
    {
        RunStore.CheckPrivate(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
        RunStore.CheckPrivate(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if ((File.GetAttributes(keyPath) & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.AuthenticationFailed);
        var config = Wire.Decode<RunConfig>(File.ReadAllText(configPath)); config.Validate();
        if (config.ExpiresAt <= DateTimeOffset.UtcNow || config.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(15)) throw new DriverException(Error.Expired);
        using var signer = ECDsa.Create(); signer.ImportFromPem(File.ReadAllText(keyPath));
        var signed = Authorization.Sign(config,signer); Authorization.Verify(signed);
        using var destination = new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        var bytes = System.Text.Encoding.UTF8.GetBytes(Wire.Encode(signed)); destination.Write(bytes); destination.Flush(true);
    }
}
