using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;

namespace KDS.Dashboard.Components.Pages;

public class PodLink : LinkModel
{
    public PodNode? TargetPodNode { get; init; }

    public PodLink(Anchor source, Anchor target) : base(source, target)
    {
    }

    public PodLink(string id, Anchor source, Anchor target) : base(id, source, target)
    {
    }

    public PodLink(PortModel sourcePort, PortModel targetPort) : base(sourcePort, targetPort)
    {
    }

    // public PodLink(NodeModel sourceNode, PodNode targetPodNode) : base(sourceNode, targetPodNode)
    // {
    //     TargetPodNode = targetPodNode;
    // }

    public PodLink(NodeModel sourceNode, NodeModel targetNode) : base(sourceNode, targetNode)
    {
    }

    public PodLink(string id, PortModel sourcePort, PortModel targetPort) : base(id, sourcePort, targetPort)
    {
    }

    public PodLink(string id, NodeModel sourceNode, NodeModel targetNode) : base(id, sourceNode, targetNode)
    {
    }
}