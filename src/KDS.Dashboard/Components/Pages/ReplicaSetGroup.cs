using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using k8s.Models;

namespace KDS.Dashboard.Components.Pages;

public sealed class ReplicaSetGroup : GroupModel
{
    public V1ReplicaSet Resource { get; set; }
    public string Name => Resource.Metadata.Name;
    public string? Deployment => Resource.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true && o.Kind == "Deployment")?.Name;
    public string Revision => Resource.Metadata.Annotations?.TryGetValue("deployment.kubernetes.io/revision", out var revision) == true ? revision : "—";
    public string Image => string.Join(", ", Resource.Spec.Template.Spec.Containers.Select(c => c.Image));
    public int Desired => Resource.Spec.Replicas ?? 0;
    public int Current => Resource.Status?.Replicas ?? 0;
    public int Ready => Resource.Status?.ReadyReplicas ?? 0;

    public ReplicaSetGroup(V1ReplicaSet resource, Point position) : base([], padding: 30)
    {
        Resource = resource;
        Position = position;
        Size = new Size(360, 150);
    }

    protected override bool UpdateDimensions()
    {
        if (Children.Count == 0) { Size = new Size(360, 150); return true; }
        if (Children.Any(c => c.Size is null)) return false;
        var left = Children.Min(c => c.Position.X) - 30;
        var top = Children.Min(c => c.Position.Y) - 140;
        if (Position.X != left || Position.Y != top)
        {
            Position = new Point(left, top);
            TriggerMoving();
        }
        Size = new Size(Math.Max(360, Children.Max(c => c.Position.X + c.Size!.Width) - left + 30),
            Children.Max(c => c.Position.Y + c.Size!.Height) - top + 30);
        return true;
    }
}
