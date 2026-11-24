using Blazor.Diagrams.Core.Models;

namespace KDS.Dashboard.Components.Pages;

public class DeploymentGroup(string name, IEnumerable<PodNode> pods) : GroupModel(pods, padding: 60)
{
    public string Name { get; } = name;
    public int ReadyCount => Children.OfType<PodNode>().Count(p => p.IsReady);
}
