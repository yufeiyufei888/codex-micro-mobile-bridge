using Makaretu.Dns;

namespace CodexMicroBridge.App;

public sealed class MdnsAdvertisement : IDisposable
{
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;

    public void Start(string hostId, int port, string spkiSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(spkiSha256);
        if (_discovery is not null)
        {
            throw new InvalidOperationException("mDNS advertisement is already running.");
        }

        var profile = new ServiceProfile($"Codex Micro {Environment.MachineName}", "_codexmicro._tcp", (ushort)port);
        profile.AddProperty("id", hostId);
        profile.AddProperty("proto", "1");
        profile.AddProperty("tls", spkiSha256);
        var discovery = new ServiceDiscovery();
        discovery.Advertise(profile);
        _profile = profile;
        _discovery = discovery;
    }

    public void Stop()
    {
        if (_discovery is null)
        {
            return;
        }

        if (_profile is not null)
        {
            _discovery.Unadvertise(_profile);
        }

        _discovery.Dispose();
        _discovery = null;
        _profile = null;
    }

    public void Dispose() => Stop();
}
