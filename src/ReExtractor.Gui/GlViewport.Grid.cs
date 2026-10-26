using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace ReExtractor.Gui;

public sealed partial class GlViewport
{
    // RE source meshes are in metres; the render-boundary basis transform has no scale.
    private const float GridMinorSpacing = 0.1f;
    private const float GridMajorSpacing = 1f;
    private uint _groundProgram, _groundVao;
    private int _groundEye, _groundRight, _groundUp, _groundForward, _groundRayScale, _groundOrthographic, _groundSpacing;
    private string _groundError = "NoError";

    private void InitGroundGrid()
    {
        var g = _gl!;
        _groundProgram = CreateProgram(GroundVert, GroundFrag);
        _groundVao = g.GenVertexArray();
        _groundRight = g.GetUniformLocation(_groundProgram, "uRight");
        _groundUp = g.GetUniformLocation(_groundProgram, "uUp");
        _groundForward = g.GetUniformLocation(_groundProgram, "uForward");
        _groundRayScale = g.GetUniformLocation(_groundProgram, "uRayScale");
        _groundEye = g.GetUniformLocation(_groundProgram, "uEye");
        _groundOrthographic = g.GetUniformLocation(_groundProgram, "uOrthographic");
        _groundSpacing = g.GetUniformLocation(_groundProgram, "uSpacing");
    }

    private void DrawGroundGrid(GL g, float aspect)
    {
        _groundError = "NoError";
        if (!ShowGrid) return;
        var eye = GetEye();
        var forward = Vector3.Normalize(_target - eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, GetCameraUp()));
        var up = Vector3.Cross(right, forward);
        var halfHeight = Projection == ViewportProjection.Orthographic
            ? MathF.Max(0.05f, _dist * MathF.Tan(MathF.PI / 8f))
            : MathF.Tan(MathF.PI / 8f);
        g.Disable(EnableCap.DepthTest);
        g.DepthMask(false);
        g.UseProgram(_groundProgram);
        g.Uniform3(_groundRight, right.X, right.Y, right.Z);
        g.Uniform3(_groundUp, up.X, up.Y, up.Z);
        g.Uniform3(_groundForward, forward.X, forward.Y, forward.Z);
        g.Uniform2(_groundRayScale, halfHeight * MathF.Max(0.1f, aspect), halfHeight);
        g.Uniform3(_groundEye, eye.X, eye.Y, eye.Z);
        g.Uniform1(_groundOrthographic, Projection == ViewportProjection.Orthographic ? 1 : 0);
        g.Uniform2(_groundSpacing, GridMinorSpacing, GridMajorSpacing);
        g.BindVertexArray(_groundVao);
        g.DrawArrays(PrimitiveType.Triangles, 0, 3);
        g.BindVertexArray(0);
        g.DepthMask(true);
        g.Enable(EnableCap.DepthTest);
        _groundError = g.GetError().ToString();
    }

    private const string GroundVert = """
        #version 300 es
        precision highp float;
        out vec2 vNdc;
        void main() {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            vNdc = p * 2.0 - 1.0;
            gl_Position = vec4(vNdc, 0.0, 1.0);
        }
        """;

    private const string GroundFrag = """
        #version 300 es
        precision highp float;
        in vec2 vNdc;
        uniform vec3 uRight;
        uniform vec3 uUp;
        uniform vec3 uForward;
        uniform vec2 uRayScale;
        uniform vec3 uEye;
        uniform int uOrthographic;
        uniform vec2 uSpacing;
        out vec4 FragColor;
        float gridLine(vec2 world, float spacing, float width) {
            vec2 coord = world / spacing;
            vec2 footprint = max(fwidth(coord), vec2(0.00001));
            vec2 distanceToLine = abs(fract(coord - 0.5) - 0.5) / footprint;
            vec2 line = clamp(vec2(width * 0.5 + 0.5) - distanceToLine, 0.0, 1.0);
            // Fade the cell as a whole at grazing angles; preserving one line family
            // after the other becomes sub-pixel produces broad bands at the horizon.
            float density = max(footprint.x, footprint.y);
            return max(line.x, line.y) * (1.0 - smoothstep(0.10, 0.35, density));
        }
        void main() {
            // Build rays from the camera basis. Subtracting inverse-projected world
            // positions loses angular precision when near is tiny or the camera is far away.
            vec3 offset = uRight * (vNdc.x * uRayScale.x) + uUp * (vNdc.y * uRayScale.y);
            vec3 direction = uOrthographic == 1 ? uForward : normalize(uForward + offset);
            vec3 origin = uOrthographic == 1 ? uEye + offset : uEye;
            if (abs(direction.z) < 0.000001) discard;
            float t = -origin.z / direction.z;
            if (t <= 0.0) discard;
            vec2 world = (origin + direction * t).xy;
            float grazingFade = smoothstep(0.015, 0.10, abs(direction.z));
            float minor = gridLine(world, uSpacing.x, 0.8) * grazingFade;
            float major = gridLine(world, uSpacing.y, 1.6) * grazingFade;
            vec3 color = mix(vec3(0.105, 0.115, 0.13), vec3(0.27, 0.34, 0.43), minor * 0.70);
            color = mix(color, vec3(0.48, 0.59, 0.73), major * 0.90);
            FragColor = vec4(color, 1.0);
        }
        """;
}
