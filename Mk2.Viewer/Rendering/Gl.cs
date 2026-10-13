using System;
using System.Collections.Generic;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace Mk2.Viewer.Rendering
{
    /// <summary>Small helpers around raw GL objects. All math uses System.Numerics (row-vector convention).</summary>
    public static class GlUtil
    {
        /// <summary>
        /// Uploads a System.Numerics matrix. Its row-major memory read as column-major by GLSL
        /// gives the transpose, so shaders compute "M * v" which equals (v * M) on the CPU side.
        /// </summary>
        public static void SetMatrix(int location, Matrix4x4 m)
        {
            if (location < 0) return;
            var a = new float[]
            {
                m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
            };
            GL.UniformMatrix4(location, 1, false, a);
        }

        /// <summary>OpenGL-style (-1..1 depth) right-handed perspective, row-vector convention.</summary>
        public static Matrix4x4 Perspective(float fovY, float aspect, float near, float far)
        {
            float f = 1f / MathF.Tan(fovY / 2f);
            return new Matrix4x4(
                f / aspect, 0, 0, 0,
                0, f, 0, 0,
                0, 0, (far + near) / (near - far), -1,
                0, 0, 2 * far * near / (near - far), 0);
        }
    }

    public sealed class ShaderProgram : IDisposable
    {
        public int Handle { get; private set; }
        private readonly Dictionary<string, int> _locations = new Dictionary<string, int>();

        public ShaderProgram(string vs, string fs)
        {
            int v = Compile(ShaderType.VertexShader, vs);
            int f = Compile(ShaderType.FragmentShader, fs);
            Handle = GL.CreateProgram();
            GL.AttachShader(Handle, v);
            GL.AttachShader(Handle, f);
            GL.LinkProgram(Handle);
            GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new Exception("Shader link failed: " + GL.GetProgramInfoLog(Handle));
            GL.DetachShader(Handle, v);
            GL.DetachShader(Handle, f);
            GL.DeleteShader(v);
            GL.DeleteShader(f);
        }

        private static int Compile(ShaderType type, string src)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, src);
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new Exception(type + " compile failed: " + GL.GetShaderInfoLog(s));
            return s;
        }

        public void Use() => GL.UseProgram(Handle);

        public int Loc(string name)
        {
            if (!_locations.TryGetValue(name, out int l))
            {
                l = GL.GetUniformLocation(Handle, name);
                _locations[name] = l;
            }
            return l;
        }

        public void Set(string name, Matrix4x4 m) => GlUtil.SetMatrix(Loc(name), m);
        public void Set(string name, float v) { int l = Loc(name); if (l >= 0) GL.Uniform1(l, v); }
        public void Set(string name, int v) { int l = Loc(name); if (l >= 0) GL.Uniform1(l, v); }
        public void Set(string name, bool v) => Set(name, v ? 1 : 0);
        public void Set(string name, Vector2 v) { int l = Loc(name); if (l >= 0) GL.Uniform2(l, v.X, v.Y); }
        public void Set(string name, Vector3 v) { int l = Loc(name); if (l >= 0) GL.Uniform3(l, v.X, v.Y, v.Z); }
        public void Set(string name, Vector4 v) { int l = Loc(name); if (l >= 0) GL.Uniform4(l, v.X, v.Y, v.Z, v.W); }

        public void Dispose()
        {
            if (Handle != 0) GL.DeleteProgram(Handle);
            Handle = 0;
        }
    }

    /// <summary>Indexed triangle mesh with interleaved pos(3) nrm(3) uv(2) col(4).</summary>
    public sealed class GpuMesh : IDisposable
    {
        public const int Stride = 12;
        public int Vao, Vbo, Ebo, IndexCount;

        public static GpuMesh Create(float[] interleaved, uint[] indices)
        {
            var m = new GpuMesh { IndexCount = indices.Length };
            m.Vao = GL.GenVertexArray();
            GL.BindVertexArray(m.Vao);
            m.Vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, m.Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, interleaved.Length * sizeof(float), interleaved, BufferUsageHint.StaticDraw);
            m.Ebo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, m.Ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
            int stride = Stride * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            GL.EnableVertexAttribArray(3);
            GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
            GL.BindVertexArray(0);
            return m;
        }

        public void Draw()
        {
            if (IndexCount == 0) return;
            GL.BindVertexArray(Vao);
            GL.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, 0);
        }

        public void Dispose()
        {
            if (Vao != 0) GL.DeleteVertexArray(Vao);
            if (Vbo != 0) GL.DeleteBuffer(Vbo);
            if (Ebo != 0) GL.DeleteBuffer(Ebo);
            Vao = Vbo = Ebo = 0;
        }
    }

    /// <summary>Line list with pos(3) + rgba(4) per vertex.</summary>
    public sealed class GpuLines : IDisposable
    {
        public int Vao, Vbo, VertexCount;

        public static GpuLines Create(List<float> data)
        {
            var l = new GpuLines { VertexCount = data.Count / 7 };
            l.Vao = GL.GenVertexArray();
            GL.BindVertexArray(l.Vao);
            l.Vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, l.Vbo);
            var arr = data.ToArray();
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
            GL.BindVertexArray(0);
            return l;
        }

        public void Draw()
        {
            if (VertexCount == 0) return;
            GL.BindVertexArray(Vao);
            GL.DrawArrays(PrimitiveType.Lines, 0, VertexCount);
        }

        public void Dispose()
        {
            if (Vao != 0) GL.DeleteVertexArray(Vao);
            if (Vbo != 0) GL.DeleteBuffer(Vbo);
            Vao = Vbo = 0;
        }
    }

    public static class Shaders
    {
        public const string MeshVS = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNrm;
layout(location=2) in vec2 aUV;
layout(location=3) in vec4 aCol;
uniform mat4 uModel;
uniform mat4 uViewProj;
uniform vec2 uUVOffset;
out vec3 vNrm;
out vec2 vUV;
out vec4 vCol;
out vec3 vWorld;
void main() {
    vec4 w = uModel * vec4(aPos, 1.0);
    vWorld = w.xyz;
    gl_Position = uViewProj * w;
    vNrm = mat3(uModel) * aNrm;
    vUV = aUV + uUVOffset;
    vCol = aCol;
}";

        public const string MeshFS = @"#version 330 core
in vec3 vNrm;
in vec2 vUV;
in vec4 vCol;
in vec3 vWorld;
uniform sampler2D uTex;
uniform int uUseTex;
uniform int uUseVCol;
uniform int uLighting;
uniform float uAlphaRef;
uniform float uBrightness;
uniform vec4 uTint;
uniform vec3 uLightDir;
uniform int uFog;
uniform vec3 uFogColor;
uniform vec2 uFogRange;
uniform vec3 uEye;
out vec4 frag;
void main() {
    vec4 c = uTint;
    if (uUseTex == 1) c *= texture(uTex, vUV);
    if (uUseVCol == 1) c *= vec4(vCol.rgb * uBrightness, vCol.a);
    if (c.a < uAlphaRef) discard;
    if (uLighting == 1) {
        vec3 n = normalize(vNrm);
        if (!gl_FrontFacing) n = -n;
        float d = dot(n, normalize(uLightDir));
        c.rgb *= 0.45 + 0.55 * max(d, 0.0) + 0.15 * max(-d, 0.0);
    }
    if (uFog == 1) {
        float dist = length(vWorld - uEye);
        float f = clamp((dist - uFogRange.x) / max(uFogRange.y - uFogRange.x, 1.0), 0.0, 1.0);
        c.rgb = mix(c.rgb, uFogColor, f);
    }
    frag = c;
}";

        public const string LineVS = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec4 aCol;
uniform mat4 uViewProj;
uniform mat4 uModel;
out vec4 vCol;
void main() {
    gl_Position = uViewProj * (uModel * vec4(aPos, 1.0));
    vCol = aCol;
}";

        public const string LineFS = @"#version 330 core
in vec4 vCol;
uniform vec4 uTint;
out vec4 frag;
void main() { frag = vCol * uTint; }";
    }
}
