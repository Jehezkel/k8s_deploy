using k8s.Models;

namespace KDS.Dashboard.Services;

public static class PodDeploymentOwnership
{
    public static V1OwnerReference? Resolve(V1Pod pod, IReadOnlyDictionary<string, V1ReplicaSet> replicaSets)
    {
        var owner = pod.Metadata.OwnerReferences?
            .FirstOrDefault(o => o.Controller == true && o.Kind == "ReplicaSet");
        if (owner is null || !replicaSets.TryGetValue(owner.Uid, out var replicaSet) ||
            replicaSet.Metadata.NamespaceProperty != pod.Metadata.NamespaceProperty)
            return null;

        return replicaSet.Metadata.OwnerReferences?
            .FirstOrDefault(o => o.Controller == true && o.Kind == "Deployment");
    }
}
