using System.Collections.Concurrent;
using DotNative;
using DotNative.Composition;

var tests = new (string Name, Action Run)[]
{
    ("Watch, equal state, Busy and clean siblings", Watch),
    ("Select ignores unrelated fields and Busy", SelectSlice),
    ("Exclusive completion, suppression and failures", Exclusive),
    ("Build and worker mutation guards", Mutation),
    ("Container lifetime and overrides", Container),
};
foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

static void Require(bool value)
{
    if (!value) throw new Exception("Assertion failed.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static void Watch()
{
    var dispatcher = new Dispatcher();
    var store = new Counter(dispatcher);
    var reader = new Probe(() => store.State.ToString());
    var busy = new Probe(() => store.Busy.ToString());
    var sibling = new Probe(() => "unrelated");
    var root = new Root(reader, busy, sibling);
    using var host = new RenderHost(root, dispatcher, new Sink());
    host.RenderNow();
    store.Set(1);
    dispatcher.Drain();
    Require(reader.Builds == 2 && reader.Last == "1" && sibling.Builds == 1 && root.Builds == 1);
    store.Set(1);
    dispatcher.Drain();
    Require(reader.Builds == 2 && busy.Builds == 2);
    var completion = new TaskCompletionSource();
    var work = store.RunExclusive(() => completion.Task);
    dispatcher.Drain();
    Require(store.Busy && busy.Last == "True" && sibling.Builds == 1);
    Task.Run(() => completion.SetResult()).GetAwaiter().GetResult();
    work.GetAwaiter().GetResult();
    dispatcher.Drain();
    Require(!store.Busy && busy.Last == "False" && sibling.Builds == 1 && root.Builds == 1);
}
static void SelectSlice()
{
    var dispatcher = new Dispatcher();
    var store = new PersonStore(dispatcher);
    var name = store.Select(person => person.Name);
    var nameProbe = new Probe(() => name.Value);
    var all = new Probe(() => store.State.Name + store.State.Age);
    var root = new Root(nameProbe, all);
    using var host = new RenderHost(root, dispatcher, new Sink());
    host.RenderNow();
    store.SetAge(2);
    dispatcher.Drain();
    Require(nameProbe.Builds == 1 && all.Builds == 2 && name.Value == "ada");
    var busy = store.RunExclusive(() => Task.CompletedTask);
    busy.GetAwaiter().GetResult();
    dispatcher.Drain();
    Require(nameProbe.Builds == 1 && all.Builds == 3);
    store.SetName("bea");
    dispatcher.Drain();
    Require(nameProbe.Builds == 2 && nameProbe.Last == "bea" && all.Builds == 4);
    name.Dispose();
    store.SetName("cy");
    dispatcher.Drain();
    Require(nameProbe.Builds == 2 && store.State.Name == "cy");
}
static void Exclusive()
{
    var dispatcher = new Dispatcher();
    var store = new Counter(dispatcher);
    var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var work = store.RunExclusive<string>(() => completion.Task);
    Require(store.Busy);
    bool ran = false;
    Require(store.RunExclusive<string>(() => { ran = true; return Task.FromResult<string?>("bad"); }).Result is null);
    store.RunExclusive(() => { ran = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
    Require(!ran);
    Task.Run(() => completion.SetResult("done")).GetAwaiter().GetResult();
    Require(work.GetAwaiter().GetResult() == "done");
    Require(store.Busy); // Worker completion posts cleanup; UI queue owns the mutation.
    dispatcher.Drain();
    Require(!store.Busy);
    store.RunExclusive(() => Task.CompletedTask).GetAwaiter().GetResult();
    Require(!store.Busy); // Immediate completion clears inline on UI thread.
    Throws<ArgumentException>(() => store.RunExclusive((Func<Task>)(() => throw new ArgumentException())).GetAwaiter().GetResult());
    Require(!store.Busy);
    var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    work = store.RunExclusive<string>(async () => { await failure.Task; return "never"; });
    failure.SetException(new ArgumentException());
    Throws<ArgumentException>(() => work.GetAwaiter().GetResult());
    dispatcher.Drain();
    Require(!store.Busy);
    Throws<TaskCanceledException>(() => store.RunExclusive(() => Task.FromCanceled(new CancellationToken(true))).GetAwaiter().GetResult());
    Require(!store.Busy);
}
static void Mutation()
{
    var dispatcher = new Dispatcher();
    var store = new Counter(dispatcher);
    using var host = new RenderHost(new Probe(() =>
    {
        Throws<InvalidOperationException>(() => store.Set(0));
        Throws<InvalidOperationException>(() => store.Set(1));
        Throws<InvalidOperationException>(() => store.RunExclusive(() => Task.CompletedTask).GetAwaiter().GetResult());
        Throws<InvalidOperationException>(() => store.Select(value => value));
        return store.State.ToString();
    }), dispatcher, new Sink());
    host.RenderNow();
    Require(store.State == 0 && !store.Busy);
    Task.Run(() =>
    {
        Throws<InvalidOperationException>(() => store.Set(2));
        Throws<InvalidOperationException>(() => { _ = store.State; });
        Throws<InvalidOperationException>(() => store.RunExclusive(() => Task.CompletedTask).GetAwaiter().GetResult());
        dispatcher.Post(() => store.Set(3));
    }).GetAwaiter().GetResult();
    // Unmount the mutation-checking component before the posted update.
    host.Dispose();
    dispatcher.Drain();
    Require(store.State == 3);
}
static void Container()
{
    int created = 0;
    var order = new List<string>();
    var container = new ProviderContainer()
        .Register<Resource>(_ => { created++; return new Resource("first", order); })
        .Register<Dependent>(c => new Dependent(c.Get<Resource>(), order));
    Require(created == 0 && !container.TryGet<string>(out _));
    var dependent = container.Get<Dependent>();
    Require(created == 1 && ReferenceEquals(dependent.Dependency, container.Get<Resource>()));
    Require(container.TryGet<Dependent>(out var found) && ReferenceEquals(dependent, found));
    var replacement = new Resource("borrowed", order);
    container.Override(replacement);
    Require(ReferenceEquals(replacement, container.Get<Resource>()));
    container.Dispose();
    container.Dispose();
    Require(order.SequenceEqual(new[] { "dependent", "first" }));
    Throws<ObjectDisposedException>(() => container.Get<Resource>());
    Throws<ObjectDisposedException>(() => container.TryGet<Resource>(out _));
    using var before = new ProviderContainer().Register<Resource>(_ => throw new Exception("Factory must be suppressed."));
    before.Override(replacement);
    Require(ReferenceEquals(before.Get<Resource>(), replacement));
    using var added = new ProviderContainer().Add(replacement);
    Require(ReferenceEquals(added.Get<Resource>(), replacement));
    using var lazy = new ProviderContainer().Register<Resource>(_ => { created++; return new Resource("lazy", order); });
    Require(created == 1 && lazy.TryGet<Resource>(out _) && created == 2);
    using var circular = new ProviderContainer().Register<Resource>(c => c.Get<Resource>());
    Throws<InvalidOperationException>(() => circular.Get<Resource>());
    var shared = new Resource("shared", order);
    var aliases = new ProviderContainer().Register<Resource>(_ => shared).Register<IDisposable>(c => c.Get<Resource>());
    aliases.Get<IDisposable>();
    aliases.Dispose();
    Require(order.Count(x => x == "shared") == 1);
    var failing = new ProviderContainer().Register<Resource>(_ => new Resource("last", order))
        .Register<IDisposable>(_ => new FailingResource());
    failing.Get<Resource>();
    failing.Get<IDisposable>();
    Throws<AggregateException>(() => failing.Dispose());
    failing.Dispose();
    Require(order.Contains("last"));
}
sealed class Counter(IUiDispatcher dispatcher) : Notifier<int>(dispatcher, 0)
{
    public void Set(int value) => SetState(value);
}
sealed record Person(string Name, int Age);
sealed class PersonStore(IUiDispatcher dispatcher) : Notifier<Person>(dispatcher, new("ada", 1))
{
    public void SetName(string name) => SetState(State with { Name = name });
    public void SetAge(int age) => SetState(State with { Age = age });
}
sealed class Dispatcher : IUiDispatcher
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<Action> _queue = new();
    public bool CheckAccess() => Environment.CurrentManagedThreadId == _thread;
    public void Post(Action action) => _queue.Enqueue(action);
    public void Drain()
    {
        if (!CheckAccess()) throw new InvalidOperationException();
        while (_queue.TryDequeue(out var action)) action();
    }
}
sealed class Sink : ICommandTransport
{
    public void Flush(ReadOnlySpan<byte> buffer) { }
}
sealed class Probe(Func<string> read) : Component
{
    public int Builds { get; private set; }
    public string? Last { get; private set; }
    public override Element Build() { Builds++; Last = read(); return new Text(Last); }
}
sealed class Root(params Element[] children) : Component
{
    public int Builds { get; private set; }
    public override Element Build() { Builds++; return new VStack(children); }
}
sealed class Resource(string name, List<string> order) : IDisposable
{
    public void Dispose() => order.Add(name);
}
sealed class Dependent(Resource dependency, List<string> order) : IDisposable
{
    public Resource Dependency { get; } = dependency;
    public void Dispose() => order.Add("dependent");
}
sealed class FailingResource : IDisposable
{
    public void Dispose() => throw new InvalidOperationException("Dispose failed.");
}
