using System.Runtime.CompilerServices;
using KDS.Dashboard.Components.Pages;
using KDS.Dashboard.Services;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;

static class LiveTopologyChecks
{
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
    static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new Exception($"Expected rejection: {name}");
    }

    public static async Task RunAsync(Func<FakeRunner, string, HelmService> service)
    {
        var yaml = await service(new(), "kds-test").ReadValuesAsync();
        var values = new DeployValues(yaml);
        values.Lines.Single(x => x.Path == "replicaCount").Value = "4";
        values.Lines.Single(x => x.Path == "ingress.host").Value = "true";
        values.Lines.Single(x => x.Path == "ingress.enabled").Value = "false";
        var serialized = values.Serialize();
        DeployValues.ValidateShape(yaml, serialized);
        var roundTrip = new DeployValues(serialized);
        Check(roundTrip.Lines.Single(x => x.Path == "replicaCount").Kind == "integer", "integer preserved");
        Check(roundTrip.Lines.Single(x => x.Path == "ingress.host").Kind == "string", "string looking like boolean stays string");
        Check(roundTrip.Lines.Single(x => x.Path == "ingress.enabled").Kind == "boolean", "boolean preserved");
        await Reject(() => Task.Run(() => DeployValues.ValidateShape(yaml, serialized + "extra: true\n")), "extra key");
        await Reject(() => Task.Run(() => DeployValues.ValidateShape(yaml, serialized.Replace("replicaCount:", "replicas:"))), "renamed key");
        values.Lines.Single(x => x.Path == "replicaCount").Value = "not a number";
        await Reject(() => Task.Run(() => values.Serialize()), "invalid numeric input");

        var runner = new FakeRunner { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var tests = new DeploymentTestService(service(runner, "kds-test"), NullLogger<DeploymentTestService>.Instance);
        await tests.StartAsync(CancellationToken.None);
        await tests.StartTestAsync(yaml, false);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(tests.Snapshot.Running, "test remains active after start returns without dialog");
        await Reject(() => tests.StartTestAsync(yaml, false), "background concurrency lock");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tests.Changed += () => { if (!tests.Snapshot.Running) completed.TrySetResult(); };
        runner.Hold.SetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(tests.Snapshot.Error is null && tests.Snapshot.Messages.Any(m => m.Contains("Gotowe")), "background completion snapshot");
        await tests.StopAsync(CancellationToken.None);
        await Reject(() => tests.StartTestAsync(yaml, false), "stopped service rejects start");

        using var failed = new DeploymentTestService(service(new() { FailOn = "install" }, "kds-test"), NullLogger<DeploymentTestService>.Instance);
        var failedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failed.Changed += () => { if (!failed.Snapshot.Running && failed.Snapshot.Error is not null) failedDone.TrySetResult(); };
        await failed.StartAsync(CancellationToken.None);
        await failed.StartTestAsync(yaml, false);
        await failedDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(failed.Snapshot.Error!.Contains("Simulated"), "background error visible");
        await failed.StopAsync(CancellationToken.None);

        CheckTopology();
        await CheckWatch();
        await CheckWatchTransport();
        Console.WriteLine("PASS: typed values, fixed shape, background lifecycle, ReplicaSet reconciliation, watch reconnect/relist/cancellation.");
    }

    static V1ReplicaSet ReplicaSet(string uid, int desired = 1) => new()
    {
        Metadata = new() { Uid = uid, Name = uid, NamespaceProperty = "kds-test", ResourceVersion = "1",
            OwnerReferences = [new() { ApiVersion = "apps/v1", Kind = "Deployment", Name = "demo-target", Uid = "deployment", Controller = true }] },
        Spec = new() { Replicas = desired, Template = new() { Spec = new() { Containers = [new() { Name = "target", Image = "kds-target:v2" }] } } },
        Status = new() { Replicas = desired, ReadyReplicas = desired }
    };

    static V1Pod Pod(string uid, string rs, string version = "1", bool ready = false) => new()
    {
        Metadata = new() { Uid = uid, Name = uid, NamespaceProperty = "kds-test", ResourceVersion = version,
            OwnerReferences = [new() { ApiVersion = "apps/v1", Kind = "ReplicaSet", Name = rs, Uid = rs, Controller = true }] },
        Status = new() { Phase = "Running", Conditions = [new() { Type = "Ready", Status = ready ? "True" : "False" }] }
    };

    static void CheckTopology()
    {
        var topology = new TopologyDiagram();
        var pod = Pod("pod-1", "rs-1");
        topology.Reconcile([pod], []);
        var node = topology.Pods["pod-1"];
        Check(node.Group is null, "unknown owner pod visible");
        topology.Reconcile([pod], [ReplicaSet("rs-1"), ReplicaSet("rs-2", 0)]);
        Check(node.Group == topology.Groups["rs-1"], "late ReplicaSet assigns existing pod");
        Check(topology.Groups["rs-2"].Children.Count == 0 && topology.Groups["rs-2"].Size!.Width > 0, "empty ReplicaSet visible");
        node.SetPosition(777, 333);
        topology.Diagram.SetPan(42, 24);
        topology.Reconcile([Pod("pod-1", "rs-1", "2", true)], [ReplicaSet("rs-1"), ReplicaSet("rs-2", 0)]);
        Check(ReferenceEquals(node, topology.Pods["pod-1"]) && node.Position.X == 777 && node.IsReady, "update retains node and position");
        Check(topology.Diagram.Pan.X == 42, "update retains viewport");
        topology.Reconcile([], [ReplicaSet("rs-1", 0)]);
        Check(topology.Pods.Count == 0 && topology.Diagram.Links.Count == 0 && topology.Groups["rs-1"].Children.Count == 0, "delete removes links and membership");
        topology.Reconcile([], []);
        Check(topology.Groups.Count == 0, "namespace reset clears groups");
        topology.Reconcile([Pod("new-pod", "new-rs")], [ReplicaSet("new-rs")]);
        Check(topology.Groups["new-rs"].Children.Count == 1, "namespace recreation");
    }

    static async Task CheckWatchTransport()
    {
        var pipe = new System.IO.Pipelines.Pipe();
        var handler = new WatchHandler(pipe.Reader.AsStream());
        using var client = new KubernetesWatchClient(new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, handler);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var iterator = client.WatchResourcesAsync<V1Pod>("api/v1/namespaces/test/pods", "123", () => opened.TrySetResult(), ct.Token).GetAsyncEnumerator();
        var first = iterator.MoveNextAsync().AsTask();
        await opened.Task.WaitAsync(ct.Token);
        Check(!first.IsCompleted, "idle watch connected before first event");
        Check(handler.RequestUri!.Query.Contains("resourceVersion=123") && handler.RequestUri.Query.Contains("watch=true"), "watch request version");
        var pod = Pod("transport-pod", "rs", "124");
        var payload = KubernetesJson.Serialize(new { type = "ADDED", @object = pod }) + "\n";
        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(payload), ct.Token);
        Check(await first && iterator.Current.Item1 == WatchEventType.Added && iterator.Current.Item2.Metadata.Uid == "transport-pod", "stream event parsed");
        var error = iterator.MoveNextAsync().AsTask();
        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"ERROR\",\"object\":{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"code\":410,\"reason\":\"Expired\"}}\n"), ct.Token);
        try { await error; throw new Exception("Expected streamed 410"); }
        catch (KubernetesException ex) { Check(ex.Status.Code == 410, "streamed error preserves status code"); }
        await pipe.Writer.CompleteAsync();

        var idle = new System.IO.Pipelines.Pipe();
        using var idleClient = new KubernetesWatchClient(new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, new WatchHandler(idle.Reader.AsStream()));
        using var cancel = new CancellationTokenSource();
        var idleOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var idleIterator = idleClient.WatchResourcesAsync<V1Pod>("api/v1/namespaces/test/pods", "1", () => idleOpened.TrySetResult(), cancel.Token).GetAsyncEnumerator();
        var waiting = idleIterator.MoveNextAsync().AsTask();
        await idleOpened.Task.WaitAsync(ct.Token);
        cancel.Cancel();
        try { await waiting.WaitAsync(ct.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        await idle.Writer.CompleteAsync();
    }

    private sealed class WatchHandler(Stream stream) : DelegatingHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }

    static async Task CheckWatch()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var snapshots = new List<ResourceSnapshot<V1Pod>>();
        var versions = new List<string>();
        var listed = 0;
        var attempt = 0;
        Task<ResourceList<V1Pod>> List(CancellationToken _) => Task.FromResult(++listed == 1
            ? new ResourceList<V1Pod>([Pod("initial", "rs", "10")], "10")
            : new ResourceList<V1Pod>([Pod("recreated", "rs-new", "30")], "30"));
        async IAsyncEnumerable<(WatchEventType, V1Pod)> Watch(string version, Action connected, [EnumeratorCancellation] CancellationToken token)
        {
            versions.Add(version);
            connected();
            switch (++attempt)
            {
                case 1:
                    yield return (WatchEventType.Added, Pod("added", "rs", "11"));
                    yield return (WatchEventType.Modified, Pod("added", "rs", "12", true));
                    yield return (WatchEventType.Deleted, Pod("initial", "rs", "13"));
                    yield return (WatchEventType.Bookmark, new() { Metadata = new() { ResourceVersion = "14" } });
                    throw new IOException("connection lost");
                case 2:
                    throw new KubernetesException(new V1Status { Code = 410, Message = "Expired" });
                case 3:
                    throw new KubernetesException(new V1Status { Code = 404, Message = "Namespace missing" });
                default:
                    ct.Cancel();
                    await Task.Delay(Timeout.Infinite, token);
                    break;
            }
        }
        var watcher = new ResourceWatch<V1Pod>(List, Watch, snapshot => { snapshots.Add(snapshot); return Task.CompletedTask; }, TimeSpan.Zero);
        await watcher.RunAsync(ct.Token);
        Check(versions.SequenceEqual(new[] { "10", "14", "30", "30" }), "resume bookmark version; relist after 410 and 404");
        Check(listed == 3, "only relist when necessary");
        Check(snapshots.Any(s => s.Connected && s.Items.Count == 1 && s.Items[0].Metadata.Uid == "added" && s.Items[0].Status.Conditions[0].Status == "True"), "add modify delete applied");
        Check(snapshots.Any(s => !s.Connected && s.Error?.Contains("connection lost") == true), "stale indicator after disconnect");
        Check(snapshots.Last().Items.All(p => p.Metadata.Uid == "recreated"), "relist replaces stale cache");
    }
}
