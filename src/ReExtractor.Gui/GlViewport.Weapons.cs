using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ReExtractor.Core;

namespace ReExtractor.Gui;

public sealed partial class GlViewport
{
    private sealed class WeaponAttachment
    {
        public required string Bone;
        public required string Name;
        public Matrix4x4 Local = Matrix4x4.Identity;
        public AnimationClip? Pose;
        public float PoseTime;
        public bool Visible;
        public bool FollowBodySkeleton;
    }

    public int WeaponAttachmentCount => _extras.Count(m => m.Attachment is { Visible: true });
    public string[] WeaponAttachmentNames => _extras.Where(m => m.Attachment is { Visible: true })
        .Select(m => m.Attachment!.Name).ToArray();
    public int WeaponAttachmentTextureCount => _extras.Where(m => m.Attachment is { Visible: true })
        .Sum(m => m.Mesh.Textures.Length);

    public void ClearWeaponAttachments()
    {
        GlModel[] removed;
        lock (_sceneLock)
        {
            removed = _extras.Where(m => m.Attachment != null).ToArray();
            _extras.RemoveAll(m => m.Attachment != null);
        }
        RetireModels(removed);
        RecalculateVisibleBounds();
        RequestNextFrameRendering();
    }

    public void AddWeaponAttachment(ViewportMesh mesh, string name, string bone, Matrix4x4 local,
        AnimationClip? pose = null, float poseTime = 0, bool followBodySkeleton = false,
        IReadOnlyCollection<int>? visibleGroups = null)
    {
        if (!followBodySkeleton && FindWeaponTarget(bone) == null)
            throw new InvalidOperationException("角色没有武器挂点：" + bone);
        if (followBodySkeleton && !mesh.Bones.Any(b => b.Name.Equals("Hip", StringComparison.OrdinalIgnoreCase)) )
            throw new InvalidOperationException("随身体蒙皮的武器缺少身体骨架：" + name);
        var model = CreateModel(mesh, false);
        if (visibleGroups != null)
        {
            if (visibleGroups.Count == 0 || visibleGroups.Any(key => !mesh.Groups.Any(g => g.Key == key)))
                throw new InvalidOperationException("武器显示部件无效：" + name);
            model.VisibleGroups = visibleGroups.ToHashSet();
            QueueIndexUpdate(model);
        }
        model.Attachment = new WeaponAttachment { Name = name, Bone = bone, Local = local, Pose = pose, PoseTime = poseTime,
            FollowBodySkeleton = followBodySkeleton };
        model.Tracks = BuildTrackMap(model);
        EvaluateWeaponAttachment(model);
        lock (_sceneLock) _extras.Add(model);
        RecalculateVisibleBounds();
        RequestNextFrameRendering();
    }

    private Matrix4x4? FindWeaponTarget(string name)
    {
        var driver = GetPoseDriver();
        var models = (driver == null ? Array.Empty<GlModel>() : new[] { driver })
            .Concat(SnapshotSceneModels().Where(m => m.Attachment == null && !ReferenceEquals(m, driver)));
        foreach (var model in models)
        {
            var index = Array.FindIndex(model.Mesh.Bones, b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < model.BoneGlobals.Length) return model.BoneGlobals[index];
        }
        return null;
    }

    private void EvaluateWeaponAttachment(GlModel model)
    {
        var attachment = model.Attachment!;
        if (attachment.FollowBodySkeleton)
        {
            attachment.Visible = GetPoseDriver() != null;
            if (_clip != null) EvaluatePose(model, _time);
            RebuildInterleaved(model);
            return;
        }
        var target = FindWeaponTarget(attachment.Bone);
        attachment.Visible = target != null;
        if (target == null) return;
        // System.Numerics uses row vectors: weapon local comes before the animated body joint.
        var transform = attachment.Local * target.Value;
        Matrix4x4.Invert(transform, out var inverse);
        var normalsTransform = Matrix4x4.Transpose(inverse);
        var mesh = model.Mesh;
        if (attachment.Pose != null)
        {
            var computed = new bool[mesh.Bones.Length];
            for (var b = 0; b < computed.Length; b++)
                ComputeGlobal(model, b, computed, attachment.PoseTime);
        }
        else ComputeBindBoneGlobals(model);
        for (var j = 0; j < mesh.DeformToBone.Length; j++)
        {
            var bone = mesh.DeformToBone[j];
            model.JointMats[j] = mesh.Bones[bone].InverseGlobalBind * model.BoneGlobals[bone];
        }
        for (var i = 0; i < mesh.Vertices.Length; i++)
        {
            var point = mesh.Vertices[i];
            var normal = mesh.Normals[i];
            if (attachment.Pose != null && i < mesh.Weights.Length && mesh.Weights[i].Length > 0)
            {
                var p = Vector3.Zero;
                var n = Vector3.Zero;
                foreach (var (joint, weight) in mesh.Weights[i])
                {
                    if ((uint)joint >= (uint)model.JointMats.Length) continue;
                    p += Vector3.Transform(point, model.JointMats[joint]) * weight;
                    n += Vector3.TransformNormal(normal, model.JointMats[joint]) * weight;
                }
                point = p;
                normal = n;
            }
            model.Posed[i] = Vector3.Transform(point, transform);
            var transformedNormal = Vector3.TransformNormal(normal, normalsTransform);
            model.PosedNormals[i] = transformedNormal.LengthSquared() > 1e-10f ? Vector3.Normalize(transformedNormal) : normal;
        }
        for (var i = 0; i < mesh.Bones.Length; i++) model.BoneGlobals[i] *= transform;
        RebuildInterleaved(model);
    }
}
