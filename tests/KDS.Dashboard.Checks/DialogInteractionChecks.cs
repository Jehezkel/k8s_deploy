#pragma warning disable BL0006 // This check intentionally exercises the renderer's event dispatch API.
using KDS.Dashboard.Components.Pages;
using KDS.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

static class DialogInteractionChecks
{
    public static async Task RunAsync(HelmService helm)
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton(helm)
            .AddSingleton<DeploymentTestService>().AddSingleton<IJSRuntime, FocusJs>().BuildServiceProvider();
        var tests = services.GetRequiredService<DeploymentTestService>();
        var closed = false;
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(new Dictionary<string, object?> { [nameof(DeployDialog.Close)] = EventCallback.Factory.Create(new object(), () => closed = true) });
            await renderer.Edit("value-replicaCount", "4");
            await renderer.Click("Dalej");
            await renderer.Toggle("load-enabled", true);
            await renderer.Edit("load-users", "0");
            await renderer.Click("Rozpocznij test");
            Check(!tests.Snapshot.Running && renderer.Text().Contains("1–1000"), "load validation blocks confirmation");
            await renderer.Edit("load-users", "17");
            await renderer.Edit("load-before", "7");
            await renderer.Edit("load-after", "11");
            await renderer.Click("Rozpocznij test");
            Check(!tests.Snapshot.Running && !closed, "confirmation does not start test");
            await renderer.Click("Wróć do load testu");
            Check(renderer.Value("load-users") == "17" && renderer.Value("load-before") == "7" && renderer.Value("load-after") == "11", "back preserves load settings");
            await renderer.Click("Wróć do edycji");
            Check(renderer.Value("value-replicaCount") == "4", "cancel confirmation preserves edited value");
            await renderer.Click("Dalej");
            await renderer.Click("Rozpocznij test");
            await renderer.Click("Potwierdź i rozpocznij");
            Check(tests.Snapshot.Running && closed, "accepted test closes dialog without awaiting work");
        });
        // The hosted worker is deliberately not started here: no command is executed by UI checks.
        Console.WriteLine("PASS: confirmation, cancellation retains values, accepted start closes dialog.");
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    // Browser focus is verified in the real UI; this renderer has no DOM element references.
    sealed class TestDialog : DeployDialog
    {
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
    }

    sealed class EventRenderer(IServiceProvider services, ILoggerFactory logger) : Renderer(services, logger)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private int _root;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw new Exception("UI check failed", exception);
        public Task Mount(Dictionary<string, object?> parameters)
        {
            _root = AssignRootComponentId(InstantiateComponent(typeof(TestDialog)));
            return RenderRootComponentAsync(_root, ParameterView.FromDictionary(parameters));
        }
        private RenderTreeFrame[] Element(Func<RenderTreeFrame[], bool> match)
        {
            var frames = GetCurrentRenderTreeFrames(_root);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element) continue;
                var element = frames.Array.Skip(i).Take(frames.Array[i].ElementSubtreeLength).ToArray();
                if (match(element)) return element;
            }
            throw new Exception("UI element not found: " + string.Join(" | ", frames.Array.Take(frames.Count).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent)));
        }
        private RenderTreeFrame[] Input(string id) => Element(e => e[0].ElementName == "input" && e.Any(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "id" && Equals(f.AttributeValue, id)));
        public string Text()
        {
            var frames = GetCurrentRenderTreeFrames(_root);
            return string.Join(" ", frames.Array.Take(frames.Count).Where(f => f.FrameType == RenderTreeFrameType.Text).Select(f => f.TextContent));
        }
        public Task Toggle(string id, bool value) => DispatchEventAsync(Input(id).First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onchange").AttributeEventHandlerId, null, new ChangeEventArgs { Value = value });
        public string? Value(string id) => Input(id).First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "value").AttributeValue?.ToString();
        public Task Edit(string id, string value) => DispatchEventAsync(Input(id).First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "oninput").AttributeEventHandlerId, null, new ChangeEventArgs { Value = value });
        public Task Click(string text)
        {
            var button = Element(e => e[0].ElementName == "button" && e.Any(f => (f.FrameType == RenderTreeFrameType.Text && f.TextContent == text) || (f.FrameType == RenderTreeFrameType.Markup && f.MarkupContent == text)));
            return DispatchEventAsync(button.First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick").AttributeEventHandlerId, null, new MouseEventArgs());
        }
    }

    sealed class FocusJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
