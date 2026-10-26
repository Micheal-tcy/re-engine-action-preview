using System;
using System.Numerics;

namespace ReExtractor.Gui;

public sealed partial class GlViewport
{
    private float _cameraNear = 0.001f, _cameraFar = 100f;

    private (float Near, float Far) GetClipPlanes(Vector3 eye, Vector3 forward)
    {
        var closest = float.PositiveInfinity;
        var furthest = float.NegativeInfinity;
        // Posed geometry includes root motion and attached weapons. The orbit pivot and
        // its distance no longer constrain clipping when flying or panning the camera.
        foreach (var model in SnapshotSceneModels())
        {
            if (model.Retired || model.Attachment is { Visible: false }) continue;
            var positions = model.Posed.Length > 0 ? model.Posed : model.Mesh.Vertices;
            foreach (var position in positions)
            {
                var depth = Vector3.Dot(Vector3.Transform(position, ReToUeWorld) - eye, forward);
                if (!float.IsFinite(depth)) continue;
                closest = MathF.Min(closest, depth);
                furthest = MathF.Max(furthest, depth);
            }
        }
        if (!float.IsFinite(furthest) || furthest <= 0) return (0.0005f, 100f);
        var near = MathF.Max(0.0005f, closest > 0 ? closest * 0.05f : 0.0005f);
        var far = MathF.Max(near + 1f, furthest + MathF.Max(1f, furthest * 0.1f));
        return (near, far);
    }
}
