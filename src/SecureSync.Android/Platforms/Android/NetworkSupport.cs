using Android.Content;
using Android.Net.Wifi;

namespace SecureSync.Mobile;

/// <summary>
/// Android filters out broadcast/multicast packets on Wi-Fi to save power unless an app holds a multicast lock.
/// SecureSync holds it only while syncing is running — i.e. while the app is open — and releases it immediately after.
/// </summary>
internal static class NetworkSupport
{
    private static WifiManager.MulticastLock? _lock;

    public static void BeginDiscovery()
    {
        if (_lock is { IsHeld: true }) return;
        var wifi = (WifiManager?)Android.App.Application.Context.GetSystemService(Context.WifiService);
        _lock = wifi?.CreateMulticastLock("SecureSync");
        if (_lock is null) return;
        _lock.SetReferenceCounted(false);
        _lock.Acquire();
    }

    public static void EndDiscovery()
    {
        if (_lock is { IsHeld: true }) _lock.Release();
        _lock = null;
    }
}
