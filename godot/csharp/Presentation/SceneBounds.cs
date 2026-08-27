using Godot;

namespace OpenDAO.Presentation;

internal static class SceneBounds
{
    internal static Aabb Calculate(Node node) => Calculate(node, Transform3D.Identity);

    private static Aabb Calculate(Node node, Transform3D accumulated)
    {
        var result = new Aabb();
        var hasBounds = false;
        if (node is MeshInstance3D { Mesh: not null } mesh)
        {
            result = accumulated * mesh.GetAabb();
            hasBounds = true;
        }

        foreach (var child in node.GetChildren())
        {
            var childTransform = child is Node3D child3D
                ? accumulated * child3D.Transform
                : accumulated;
            var childBounds = Calculate(child, childTransform);
            if (childBounds.Size.IsZeroApprox())
            {
                continue;
            }

            result = hasBounds ? result.Merge(childBounds) : childBounds;
            hasBounds = true;
        }

        return result;
    }
}
