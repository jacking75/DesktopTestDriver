using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace DesktopTestDriver;

public sealed record ControllerSettings(string StateDirectory, string ListenAddress = "127.0.0.1", int Port = 32550)
{
    public string PipeName => "DesktopTestDriver-" + GuiProtocol.Hash(System.Text.Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value + Path.GetFullPath(StateDirectory).ToUpperInvariant()))[..24];
}
public sealed record AgentSettings(string StateDirectory, string MachineId, string ControllerHost, int ControllerPort,
    string ControllerCertificate, int InteractiveSessionId = 0, int MaxHoldMilliseconds = 1500, int MaxStopMilliseconds = 3000);
public sealed record PeerRegistration(string MachineId, string CertificateHash);
public static class ConnectionSettings
{
    public static string Hash(X509Certificate certificate) => GuiProtocol.Hash(certificate.GetRawCertData());
    public static X509Certificate2 Certificate(StateStore store) => X509CertificateLoader.LoadPkcs12FromFile(store.PathFor("identity.pfx"), null, X509KeyStorageFlags.UserKeySet);
    public static void Initialize(StateStore store, string name)
    {
        if (store.Exists("identity.pfx")) throw new GuiException("AlreadyInitialized");
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=DesktopTestDriver-" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
        File.WriteAllBytes(store.PathFor("identity.pfx"), cert.Export(X509ContentType.Pfx));
        File.WriteAllBytes(store.PathFor("identity.cer"), cert.Export(X509ContentType.Cert));
    }
    public static void Register(StateStore store, string machine, string certificate)
    {
        if (!GuiProtocol.Id(machine)) throw new GuiException("InvalidMachineId");
        using var cert = X509CertificateLoader.LoadCertificateFromFile(certificate);
        var peers = store.Exists("peers.json") ? store.Read<PeerRegistration[]>("peers.json") : [];
        if (peers.Any(p => p.MachineId == machine || p.CertificateHash == Hash(cert))) throw new GuiException("DuplicateMachineRegistration");
        store.Write("peers.json", peers.Append(new(machine, Hash(cert))).ToArray());
    }
    public static ControllerSettings Controller(string path)
    {
        var c = GuiProtocol.Decode<ControllerSettings>(File.ReadAllText(path));
        if (!Path.IsPathFullyQualified(c.StateDirectory) || !IPAddress.TryParse(c.ListenAddress, out _) || c.Port is < 1 or > 65535) throw new GuiException("InvalidConfiguration");
        return c;
    }
    public static AgentSettings Agent(string path)
    {
        var c = GuiProtocol.Decode<AgentSettings>(File.ReadAllText(path));
        if (!Path.IsPathFullyQualified(c.StateDirectory) || !GuiProtocol.Id(c.MachineId) || string.IsNullOrWhiteSpace(c.ControllerHost) || c.ControllerPort is < 1 or > 65535 || c.MaxHoldMilliseconds is < 100 or > 1500 || c.MaxStopMilliseconds is < 100 or > 3000 || c.ControllerCertificate.Length != 64) throw new GuiException("InvalidConfiguration");
        return c;
    }
}
