using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;

namespace KDS.Dashboard.Components.Pages;

public class PodNode : NodeModel
{
    public PodNode(Point? position = null) : base(position)
    {
    }

    public string Phase { get; set; }
    public bool IsReady{ get; set; }
    public string StateReason { get; set; }
    public DateTime? DeletionTimeStamp { get; set; }
    public string IpAddress { get; set; }
}