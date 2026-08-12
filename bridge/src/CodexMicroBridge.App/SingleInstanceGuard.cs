using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CodexMicroBridge.App;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static bool TryAcquire(out SingleInstanceGuard? guard)
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        var mutex = new Mutex(initiallyOwned: true, $"Local\\CodexMicroBridge-{userHash}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
