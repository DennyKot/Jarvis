using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jarvis.Automation.Browser.Extension;

/// <summary>
/// A fixed identity for the unpacked Jarvis Bridge extension. The manifest "key" makes Chrome give it a stable id,
/// so the bridge can accept only connections from this extension (Origin) that also know the token.
/// </summary>
public sealed record ExtensionIdentity(string PublicKey, string Id, string Token)
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "ChromeExtension");

    public string Origin => $"chrome-extension://{Id}";

    public static ExtensionIdentity LoadOrCreate(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "identity.json");
        if (File.Exists(path) && JsonSerializer.Deserialize<ExtensionIdentity>(File.ReadAllText(path)) is { } existing)
        {
            return existing;
        }

        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var identity = new ExtensionIdentity(
            Convert.ToBase64String(publicKey),
            ComputeId(publicKey),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
        File.WriteAllText(path, JsonSerializer.Serialize(identity));
        return identity;
    }

    /// <summary>Chrome's extension id: the first 16 bytes of SHA-256(public key), hex digits mapped to 'a'..'p'.</summary>
    internal static string ComputeId(byte[] publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        var builder = new StringBuilder(32);
        foreach (var b in hash.AsSpan(0, 16))
        {
            builder.Append((char)('a' + (b >> 4))).Append((char)('a' + (b & 0xF)));
        }

        return builder.ToString();
    }
}

/// <summary>Writes the unpacked extension (manifest, service worker, config and page scripts) to disk.</summary>
public static class ExtensionFiles
{
    public static string Write(ExtensionIdentity identity, int port, string directory)
    {
        var target = Path.Combine(directory, "unpacked");
        Directory.CreateDirectory(target);

        File.WriteAllText(Path.Combine(target, "manifest.json"), ReadResource("manifest.json").Replace("__KEY__", identity.PublicKey));
        File.WriteAllText(Path.Combine(target, "background.js"), ReadResource("background.js"));
        File.WriteAllText(Path.Combine(target, "config.js"),
            $"const JARVIS = {{ port: {port}, token: \"{identity.Token}\" }};\n");

        // The same scripts as the DevTools mode, so snapshots, refs and diffs behave identically.
        File.WriteAllText(Path.Combine(target, "page-scripts.js"),
            $"window.__jarvis = {{\n  snapshot: {PageScripts.Snapshot},\n  readText: {PageScripts.ReadText},\n  viewport: {PageScripts.Viewport},\n}};\n");
        return target;
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Extension." + name)
            ?? throw new InvalidOperationException($"Embedded extension file '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
