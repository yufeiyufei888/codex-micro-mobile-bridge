using System.Text;
using System.Text.RegularExpressions;

namespace CodexMicroBridge.Core.Security;

public sealed partial class DpapiSecretStore
{
    private readonly string _directory;
    private readonly byte[] _entropy;

    public DpapiSecretStore(string directory, string purpose = "CodexMicroBridge.v1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _entropy = Encoding.UTF8.GetBytes(purpose);
        Directory.CreateDirectory(_directory);
    }

    public void Save(string name, ReadOnlySpan<byte> secret)
    {
        var path = GetPath(name);
        var encrypted = DpapiFieldProtector.ProtectCurrentUser(secret.ToArray(), _entropy);
        var temporary = path + ".new";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, path, overwrite: true);
    }

    public byte[]? Load(string name)
    {
        var path = GetPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var encrypted = File.ReadAllBytes(path);
        return DpapiFieldProtector.UnprotectCurrentUser(encrypted, _entropy);
    }

    private string GetPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!SafeName().IsMatch(name))
        {
            throw new ArgumentException("Secret names may contain only letters, numbers, dot, underscore, and dash.", nameof(name));
        }

        return Path.Combine(_directory, name);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}
