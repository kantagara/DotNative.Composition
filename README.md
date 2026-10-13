# DotNative.Composition

Optional first-party managed stores shaped like Riverpod/Provider, built on
DotNative's existing `ReactiveSource`. Target frameworks: .NET 9 and .NET 10.

This is not Cubit, a native plugin, or a replacement for Microsoft.Extensions.DependencyInjection.
There are no events, reducers, BlocBuilder, assembly scans, or native targets.
Apps can register notifiers as MS.DI singletons or use the small session-scoped
`ProviderContainer` below.

## Watch and SetState

Reading `State` or `Busy` inside `Component.Build` watches the notifier for that
mounted occurrence. No separate Watch method or subscription is needed.
`SetState` publishes only when `EqualityComparer<T>.Default` detects a change.
The renderer rebuilds readers and reuses clean retained sibling components.
State and Busy share one reactive source, so either change invalidates its readers.
Prefer immutable state values; mutating fields inside a state object is not observed.

```csharp
using DotNative;
using DotNative.Composition;

public sealed class Counter(IUiDispatcher dispatcher) : Notifier<int>(dispatcher, 0)
{
    public void Increment() => SetState(State + 1);
}

public sealed class CounterView(Counter counter) : Component
{
    public override Element Build() => new Text($"Count: {counter.State}");
}
```

```csharp
// Construct the store on the UI thread.
using var providers = new ProviderContainer()
    .Add<IUiDispatcher>(dispatcher)
    .Register(c => new Counter(c.Get<IUiDispatcher>()));
var counter = providers.Get<Counter>();
```

## Select

Retain a slice in a field. This is Riverpod's `ref.watch(provider.select(...))`:
Busy and unrelated fields do not rebuild that reader.

```csharp
private readonly Selected<string> _name;

public ProfileScreen(People people)
{
    _name = people.Select(person => person.Name);
}

public override Element Build() => new Text(_name.Value);
```

Do not call `Select` during Build. Dispose the slice with the screen if the
screen's lifetime is shorter than the notifier.

`State` and `Busy` reads, `SetState`, and starting `RunExclusive` belong to the UI
thread that created the store. Mutations during Build throw, including equal
SetState calls. Workers must `dispatcher.Post(() => counter.Increment())`.
Retain component instances to benefit from incremental rendering; see the
framework's [incremental rendering documentation](../dotNative/docs/incremental-rendering.md).

## RunExclusive

```csharp
await counter.RunExclusive(async () =>
{
    await SaveAsync();
});

string? result = await counter.RunExclusive<string>(async () => await LoadNameAsync());
```

Busy is the only in-flight lock. An overlapping call does not invoke its delegate:
it completes immediately with `default(TResult)` (or a completed non-generic Task).
The `TResult?` annotation follows C# unconstrained generic nullable semantics;
for a nullable value result, explicitly use a nullable type such as `int?`.

Busy changes publish to the renderer. Completion, exceptions, and cancellation
all clear Busy in `finally`. Cleanup runs immediately when the dispatcher has
access; otherwise it is posted. In that case the returned task may complete
before the UI queue clears Busy. Exceptions and cancellation propagate normally.
An operation that changes State after an await must still use the UI thread;
RunExclusive only dispatches its own Busy cleanup.

## Explicit composition and ownership

`Register<T>` stores an explicit factory. `Get<T>` lazily creates and caches one
instance; `TryGet<T>` does the same when registered, or returns false for an absent
registration. Factory failures propagate. Duplicate Register/Add calls throw;
use `Override<T>` to replace a provider before or after creation in tests.
A failed factory can be retried and circular dependencies throw.

The container owns IDisposable instances returned by factories, disposes them once
by reference identity in reverse creation order, and is idempotent. Replaced
factory-created instances remain owned until session disposal. `Add` and
`Override` borrow caller-owned instances and do not transfer disposal ownership.
All owned instances are attempted even if disposal fails; failures are aggregated.
A disposed container rejects lookups and registration. The container is intended
for one session/UI thread and is not thread-safe.

For NativeAOT, register factories explicitly: `Register(c => new Counter(...))`.
No assembly scanning or reflection discovery is used. When using MS.DI, let that
container own its singletons and pass them through Add if needed.

## Build and test

The default `DotNativeSourceRoot` is `../dotNative`. When that tree exists, the
package references its DotNative project; otherwise it references the DotNative
NuGet package, following the sibling package convention.

```sh
dotnet build -c Release
dotnet pack -c Release
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/DotNative.Composition.Tests -f net9.0
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/DotNative.Composition.Tests -f net10.0
```

.NET 10 SDK builds both frameworks; .NET 9 SDK builds only net9.0. Tests use a
queued UI dispatcher and the real RenderHost with a no-op command transport.
This repository contains the package and its tests, independently of the framework.
