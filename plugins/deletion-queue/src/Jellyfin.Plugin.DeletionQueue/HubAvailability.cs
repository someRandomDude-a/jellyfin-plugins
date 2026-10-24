namespace Jellyfin.Plugin.DeletionQueue;

public sealed class HubAvailability
{
    private int ready;
    public bool Ready => Volatile.Read(ref ready) == 1;
    public void Set(bool value) => Interlocked.Exchange(ref ready, value ? 1 : 0);
}
