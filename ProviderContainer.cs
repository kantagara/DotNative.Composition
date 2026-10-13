using System.Diagnostics.CodeAnalysis;

namespace DotNative.Composition;

// A session registry, intended for use on the session's UI thread.
public sealed class ProviderContainer : IDisposable
{
    private readonly Dictionary<Type, Func<ProviderContainer, object>> _factories = [];
    private readonly Dictionary<Type, object> _created = [];
    private readonly HashSet<Type> _resolving = [];
    private readonly List<IDisposable> _owned = [];
    private bool _disposed;

    public ProviderContainer Register<T>(Func<ProviderContainer, T> create) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(create);
        if (_created.ContainsKey(typeof(T)) || _factories.ContainsKey(typeof(T)))
            throw new InvalidOperationException($"Provider {typeof(T).Name} is already registered. Use Override to replace it.");
        _factories.Add(typeof(T), container => create(container)
            ?? throw new InvalidOperationException($"Provider {typeof(T).Name} factory returned null."));
        return this;
    }

    // Supplied instances are borrowed; their caller retains disposal ownership.
    public ProviderContainer Add<T>(T instance) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(instance);
        if (_created.ContainsKey(typeof(T)) || _factories.ContainsKey(typeof(T)))
            throw new InvalidOperationException($"Provider {typeof(T).Name} is already registered. Use Override to replace it.");
        _created.Add(typeof(T), instance);
        return this;
    }

    public T Get<T>() where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_created.TryGetValue(typeof(T), out var existing))
            return (T)existing;
        if (!_factories.TryGetValue(typeof(T), out var factory))
            throw new InvalidOperationException($"No provider registered for {typeof(T).Name}.");
        if (!_resolving.Add(typeof(T)))
            throw new InvalidOperationException($"Circular provider dependency for {typeof(T).Name}.");
        try
        {
            var instance = (T)factory(this);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _created.Add(typeof(T), instance);
            if (instance is IDisposable disposable && !ReferenceEquals(instance, this)
                && !_owned.Any(owned => ReferenceEquals(owned, disposable)))
                _owned.Add(disposable);
            return instance;
        }
        finally
        {
            _resolving.Remove(typeof(T));
        }
    }

    public bool TryGet<T>([MaybeNullWhen(false)] out T value) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_created.ContainsKey(typeof(T)) || _factories.ContainsKey(typeof(T)))
        {
            value = Get<T>();
            return true;
        }
        value = default;
        return false;
    }

    public void Override<T>(T instance) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(instance);
        _created[typeof(T)] = instance;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        List<Exception>? errors = null;
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            try { _owned[i].Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        _owned.Clear();
        _created.Clear();
        _factories.Clear();
        if (errors is not null)
            throw new AggregateException(errors);
    }
}
