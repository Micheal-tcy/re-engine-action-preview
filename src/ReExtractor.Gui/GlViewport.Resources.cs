using System.Collections.Generic;
using System.Linq;

namespace ReExtractor.Gui;

public sealed partial class GlViewport
{
    private readonly List<GlModel> _retiredModels = new();
    private int _gpuGeneration;

    // UI-side retirement only cancels work; GL deletion belongs to the render thread.
    private void RetireModels(IEnumerable<GlModel> models)
    {
        var changed = false;
        lock (_pendingLock)
        {
            foreach (var model in models)
            {
                if (model.Retired) continue;
                model.Retired = true;
                _pendingUpload.RemoveAll(item => ReferenceEquals(item.model, model));
                _pendingIndexUpdates.Remove(model);
                _pendingSelectionUpdates.Remove(model);
                _retiredModels.Add(model);
                changed = true;
            }
        }
        if (changed) RequestNextFrameRendering();
    }

    private bool PrepareModelGpuUpload(GlModel model)
    {
        if (model.Retired) return false;
        // Requeued uploads must not overwrite live handles. An old context's numeric
        // handles can be reused by a new context, so those are forgotten, never deleted.
        ReleaseModelGpuResources(model);
        model.GpuGeneration = _gpuGeneration;
        return true;
    }

    private void DrainRetiredModels()
    {
        GlModel[] retired;
        lock (_pendingLock)
        {
            retired = _retiredModels.ToArray();
            _retiredModels.Clear();
        }
        foreach (var model in retired) ReleaseModelGpuResources(model);
    }

    private void ReleaseModelGpuResources(GlModel model)
    {
        var gl = _gl;
        if (gl != null && model.GpuGeneration == _gpuGeneration)
        {
            foreach (var handle in model.TextureHandles.Distinct())
                if (handle != 0) gl.DeleteTexture(handle);
            if (model.Vao != 0) gl.DeleteVertexArray(model.Vao);
            if (model.Vbo != 0) gl.DeleteBuffer(model.Vbo);
            if (model.Ebo != 0) gl.DeleteBuffer(model.Ebo);
            if (model.EdgeEbo != 0) gl.DeleteBuffer(model.EdgeEbo);
            if (model.SelectionEbo != 0) gl.DeleteBuffer(model.SelectionEbo);
        }
        model.Vao = model.Vbo = model.Ebo = model.EdgeEbo = model.SelectionEbo = 0;
        model.TextureHandles = [];
        model.SelectionEdgeIndexCount = 0;
        model.GpuGeneration = 0;
    }

    private void ReleaseContextGpuResources()
    {
        var scene = SnapshotSceneModels();
        GlModel[] models;
        lock (_pendingLock)
        {
            models = scene.Concat(_retiredModels).Concat(_pendingUpload.Select(p => p.model))
                .Concat(_pendingIndexUpdates.Keys).Concat(_pendingSelectionUpdates.Keys).Distinct().ToArray();
            _retiredModels.Clear();
            _pendingUpload.Clear();
            _pendingIndexUpdates.Clear();
            _pendingSelectionUpdates.Clear();
        }
        foreach (var model in models) ReleaseModelGpuResources(model);
        if (_gl != null)
        {
            if (_lineVao != 0) _gl.DeleteVertexArray(_lineVao);
            if (_lineVbo != 0) _gl.DeleteBuffer(_lineVbo);
            if (_meshProgram != 0) _gl.DeleteProgram(_meshProgram);
            if (_lineProgram != 0) _gl.DeleteProgram(_lineProgram);
            if (_groundProgram != 0) _gl.DeleteProgram(_groundProgram);
            if (_groundVao != 0) _gl.DeleteVertexArray(_groundVao);
            _groundProgram = _groundVao = 0;
        }
    }
}
