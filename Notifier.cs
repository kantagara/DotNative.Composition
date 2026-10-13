namespace DotNative.Composition;

// Watchable store. Reading State/Busy during Build subscribes that occurrence.
// There is no event/reducer pipeline; methods mutate and PublishChange.
public abstract class Notifier<T> : ReactiveSource
{
    private readonly IUiDispatcher _dispatcher;
    private T _state;
    private bool _busy;

    protected Notifier(IUiDispatcher dispatcher, T state)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _state = state;
    }

    public T State
    {
        get
        {
            TrackRead();
            return _state;
        }
    }

    public bool Busy
    {
        get
        {
            TrackRead();
            return _busy;
        }
    }

    protected void SetState(T value)
    {
        VerifyCanWrite();
        if (EqualityComparer<T>.Default.Equals(_state, value))
            return;
        _state = value;
        PublishChange();
    }

    // Visible Busy is the lock. A second call returns default.
    public async Task<TResult?> RunExclusive<TResult>(Func<Task<TResult?>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        VerifyCanWrite();
        if (_busy)
            return default;
        _busy = true;
        PublishChange();
        try
        {
            return await operation();
        }
        finally
        {
            void Idle()
            {
                VerifyCanWrite();
                if (!_busy)
                    return;
                _busy = false;
                PublishChange();
            }
            if (_dispatcher.CheckAccess())
                Idle();
            else
                _dispatcher.Post(Idle);
        }
    }

    public Task RunExclusive(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunExclusive(async () =>
        {
            await operation();
            return true;
        });
    }

    // Riverpod select: retain the result in a field. Readers rebuild only when
    // the projected value changes, including when Busy is the only notification.
    // Calling Select during Build throws.
    public Selected<TSelected> Select<TSelected>(Func<T, TSelected> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        VerifyCanWrite();
        var selected = new Selected<TSelected>(selector(_state));
        selected.Bind(Listen(() => selected.Update(selector(_state))));
        return selected;
    }
}

public sealed class Selected<T> : ReactiveSource, IDisposable
{
    private readonly EqualityComparer<T> _comparer = EqualityComparer<T>.Default;
    private IDisposable? _subscription;
    private T _value;
    private bool _disposed;

    internal Selected(T value) => _value = value;

    public T Value
    {
        get
        {
            TrackRead();
            return _value;
        }
    }

    internal void Bind(IDisposable subscription) => _subscription = subscription;

    internal void Update(T value)
    {
        if (_disposed)
            return;
        VerifyCanWrite();
        if (_comparer.Equals(_value, value))
            return;
        _value = value;
        PublishChange();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription?.Dispose();
        _subscription = null;
    }
}
