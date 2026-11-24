using Blazor.Diagrams;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using k8s.Models;

namespace KDS.Dashboard.Components.Pages;

/// <summary>Reconciles watch snapshots without replacing the canvas, nodes or viewport.</summary>
public sealed class TopologyDiagram
{
    public BlazorDiagram Diagram { get; } = new();
    public IReadOnlyDictionary<string, PodNode> Pods => _pods;
    public IReadOnlyDictionary<string, ReplicaSetGroup> Groups => _groups;
    private readonly Dictionary<string, PodNode> _pods = [];
    private readonly Dictionary<string, ReplicaSetGroup> _groups = [];
    private readonly PortModel _ingress;
    private readonly Dictionary<string, int> _columns = [];
    private int Column(string owner)
    {
        if (!_columns.TryGetValue(owner, out var column)) _columns[owner] = column = _columns.Count;
        return column;
    }

    public TopologyDiagram()
    {
        Diagram.RegisterComponent<IngressNode, IngressNodeWidget>();
        Diagram.RegisterComponent<PodNode, PodNodeWidget>();
        Diagram.RegisterComponent<PodLink, PodLinkWidget>();
        Diagram.RegisterComponent<ReplicaSetGroup, ReplicaSetGroupWidget>();
        var ingress = new IngressNode(new Point(60, 280)) { Title = "http://localhost:5175" };
        Diagram.Nodes.Add(ingress);
        _ingress = ingress.AddPort(PortAlignment.Right);
    }

    public void FitView()
    {
        if (Diagram.Container is not { } container) return;
        var models = Diagram.Nodes.Cast<NodeModel>().Concat(Diagram.Groups).Where(n => n.Size is not null).ToArray();
        if (models.Length == 0) return;
        var left = models.Min(n => n.Position.X) - 30;
        var top = models.Min(n => n.Position.Y) - 30;
        var width = models.Max(n => n.Position.X + n.Size!.Width) - left + 30;
        var height = models.Max(n => n.Position.Y + n.Size!.Height) - top + 30;
        Diagram.SetZoom(Math.Min(1, Math.Min(container.Width / width, container.Height / height)));
        Diagram.SetPan((container.Width - width * Diagram.Zoom) / 2 - left * Diagram.Zoom,
            (container.Height - height * Diagram.Zoom) / 2 - top * Diagram.Zoom);
    }

    public void Reconcile(IReadOnlyList<V1Pod> pods, IReadOnlyList<V1ReplicaSet> replicaSets)
    {
        Diagram.Batch(() =>
        {
            var rsIds = replicaSets.Select(r => r.Metadata.Uid).ToHashSet();
            foreach (var uid in _groups.Keys.Where(id => !rsIds.Contains(id)).ToArray())
            {
                Diagram.Groups.Remove(_groups[uid]);
                _groups.Remove(uid);
            }
            foreach (var rs in replicaSets.OrderBy(r => r.Metadata.CreationTimestamp).ThenBy(r => r.Metadata.Name))
            {
                if (!_groups.TryGetValue(rs.Metadata.Uid, out var group))
                {
                    group = new ReplicaSetGroup(rs, new Point(390 + Column(rs.Metadata.Uid) * 430, 60));
                    _groups.Add(rs.Metadata.Uid, group);
                    Diagram.Groups.Add(group);
                }
                group.Resource = rs;
                group.Refresh();
            }
            var podIds = pods.Select(p => p.Metadata.Uid).ToHashSet();
            foreach (var uid in _pods.Keys.Where(id => !podIds.Contains(id)).ToArray())
            {
                Diagram.Nodes.Remove(_pods[uid]);
                _pods.Remove(uid);
            }
            foreach (var pod in pods.OrderBy(p => p.Metadata.CreationTimestamp).ThenBy(p => p.Metadata.Name))
            {
                var owner = pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true && o.Kind == "ReplicaSet");
                ReplicaSetGroup? group = null;
                if (owner is not null && _groups.TryGetValue(owner.Uid, out var candidate) &&
                    candidate.Resource.Metadata.NamespaceProperty == pod.Metadata.NamespaceProperty) group = candidate;
                if (!_pods.TryGetValue(pod.Metadata.Uid, out var node))
                {
                    var position = group is null
                        ? new Point(390 + Column(owner?.Uid ?? "unowned") * 430 + 30, 200 + pods.Where(p => p.Metadata.Uid != pod.Metadata.Uid &&
                            p.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true && o.Kind == "ReplicaSet")?.Uid == owner?.Uid && _pods.ContainsKey(p.Metadata.Uid)).Count() * 220)
                        : new Point(group.Position.X + 30, group.Children.Count == 0 ? group.Position.Y + 140
                            : group.Children.Max(c => c.Position.Y) + 220);
                    node = new PodNode(position);
                    _pods.Add(pod.Metadata.Uid, node);
                    Diagram.Nodes.Add(node);
                    Diagram.Links.Add(new PodLink(node.AddPort(PortAlignment.Left), _ingress) { TargetPodNode = node });
                }
                if (node.Group != group)
                {
                    node.Group?.RemoveChild(node);
                    group?.AddChild(node);
                    // New children have no measured size yet; render them before sizing the group.
                    group?.Refresh();
                }
                node.Title = pod.Metadata.Name;
                node.IpAddress = pod.Status?.PodIP ?? "";
                node.DeletionTimeStamp = pod.Metadata.DeletionTimestamp;
                node.StateReason = pod.Status?.ContainerStatuses?.Select(c => c.State?.Waiting?.Reason).FirstOrDefault(r => r is not null)
                    ?? pod.Status?.Reason ?? "";
                node.Phase = pod.Status?.Phase ?? "Pending";
                node.IsReady = pod.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;
                node.Refresh();
                foreach (var link in node.PortLinks) link.Refresh();
            }
            if (_groups.Count == 0 && _pods.Count == 0) _columns.Clear();
        });
    }
}
