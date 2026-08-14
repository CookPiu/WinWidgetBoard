namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Records process-fatal provider faults without normalizing them into a
/// recoverable provider result. The host supplies the termination policy so
/// tests can observe the boundary without terminating the test process.
/// </summary>
public sealed class ProviderProcessFatalSupervisor
{
    private readonly Action<Exception>? _terminationPolicy;
    private Exception? _fatalFault;

    public ProviderProcessFatalSupervisor(
        Action<Exception>? terminationPolicy = null)
    {
        _terminationPolicy = terminationPolicy;
    }

    public bool HasFatalFault => Volatile.Read(ref _fatalFault) is not null;

    public Exception? FatalFault => Volatile.Read(ref _fatalFault);

    public void Observe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsProcessFatal(exception))
        {
            throw new ArgumentException(
                "Only process-fatal exceptions may be supervised.",
                nameof(exception));
        }

        if (Interlocked.CompareExchange(
                ref _fatalFault,
                exception,
                comparand: null) is null)
        {
            _terminationPolicy?.Invoke(exception);
        }
    }

    public static bool IsProcessFatal(Exception exception) =>
        exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException;
}
