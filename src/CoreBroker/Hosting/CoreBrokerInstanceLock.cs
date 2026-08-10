namespace WinWidgetBoard.CoreBroker.Hosting;

public sealed class CoreBrokerInstanceLock : IDisposable
{
    private Mutex? _mutex;

    public CoreBrokerInstanceLock(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        IsAcquired = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
    }

    public bool IsAcquired { get; }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The process can be terminating after the named mutex was abandoned.
        }
        finally
        {
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
