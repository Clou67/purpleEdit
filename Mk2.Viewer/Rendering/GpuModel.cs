using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Mk2.Formats;
using Mk2.Game;
using OpenTK.Graphics.OpenGL4;

namespace Mk2.Viewer.Rendering
{
    public sealed class GpuPart
    {
        public GpuMesh Mesh;
        public Material Material;
        public int Texture;            // GL texture id, 0 = none
        public bool TextureHasAlpha;
        public bool IsCollisionComponent;
        public bool IsFragment;
        public string ComponentName;
        public int Triangles;
        public bool Transparent => Material.Blend != BlendMode.Opaque || (TextureHasAlpha && !Material.AlphaTest && !TextureIsCutout);
        public bool TextureIsCutout;
        public SkinSource Skin;
    }

    /// <summary>Per-vertex bind pose + bone data for one part, and the interleaved buffer it is skinned into.</summary>
    public sealed class SkinSource
    {
        public Vector3[] Positions, Normals;
        public short[] Bone0, Bone1;   // -1 = vertex isn't skinned (follows RigidBone, or stays put)
        public float[] Weight0;
        public int RigidBone = -1;     // component's rigid bone
        public float[] Data;           // interleaved buffer (positions/normals rewritten every pose)
    }

    /// <summary>GPU copy of a model: one part per (component, material).</summary>
    public sealed class GpuModel : IDisposable
    {
        public LoadedModel Source;
        public List<GpuPart> Parts = new List<GpuPart>();
        public Vector3 Min, Max;           // local bounds of visible geometry
        public bool HasBounds;

        /// <summary>True when built with skin data (see <see cref="ApplySkin"/>).</summary>
        public bool Animatable;

        public static GpuModel Build(LoadedModel lm, TextureCache textures, bool animatable = false)
        {
            var g = new GpuModel { Source = lm, Animatable = animatable };
            var model = lm.Model;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var groups = model.Meshes.GroupBy(m => (m.ComponentIndex, m.MaterialIndex));
            foreach (var grp in groups)
            {
                var verts = new List<ModelVertex>();
                var idx = new List<int>();
                foreach (var mesh in grp) mesh.Triangulate(verts, idx);
                if (idx.Count == 0) continue;
                var comp = model.Components[grp.Key.ComponentIndex];
                var mat = grp.Key.MaterialIndex < lm.Materials.Count ? lm.Materials[grp.Key.MaterialIndex]
                        : Material.Default(model.MaterialNames.ElementAtOrDefault(grp.Key.MaterialIndex) ?? "?");
                var data = new float[verts.Count * GpuMesh.Stride];
                for (int i = 0; i < verts.Count; i++)
                {
                    var v = verts[i];
                    int o = i * GpuMesh.Stride;
                    data[o] = v.Position.X; data[o + 1] = v.Position.Y; data[o + 2] = v.Position.Z;
                    var n = v.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(v.Normal) : Vector3.UnitY;
                    data[o + 3] = n.X; data[o + 4] = n.Y; data[o + 5] = n.Z;
                    data[o + 6] = v.UV.X; data[o + 7] = v.UV.Y;
                    data[o + 8] = v.Color.X; data[o + 9] = v.Color.Y; data[o + 10] = v.Color.Z; data[o + 11] = v.Color.W;
                    if (!comp.IsCollision)
                    {
                        min = Vector3.Min(min, v.Position);
                        max = Vector3.Max(max, v.Position);
                    }
                }
                SkinSource skin = null;
                if (animatable)
                {
                    var skinned = new HashSet<ModelVertex>();
                    foreach (var mesh in grp)
                        if (mesh.Skinned) foreach (var st in mesh.Strips) foreach (var v in st.Vertices) skinned.Add(v);
                    skin = new SkinSource
                    {
                        Positions = new Vector3[verts.Count], Normals = new Vector3[verts.Count],
                        Bone0 = new short[verts.Count], Bone1 = new short[verts.Count], Weight0 = new float[verts.Count],
                        RigidBone = comp.RigidBone, Data = data,
                    };
                    for (int i = 0; i < verts.Count; i++)
                    {
                        var v = verts[i];
                        skin.Positions[i] = v.Position;
                        skin.Normals[i] = new Vector3(data[i * GpuMesh.Stride + 3], data[i * GpuMesh.Stride + 4], data[i * GpuMesh.Stride + 5]);
                        bool sk = skinned.Contains(v);
                        skin.Bone0[i] = sk ? v.Bone0 : (short)-1;
                        skin.Bone1[i] = sk ? v.Bone1 : (short)-1;
                        skin.Weight0[i] = v.Weight0;
                    }
                }
                var tex = textures.Get(mat.TextureName);
                var part = new GpuPart
                {
                    Mesh = GpuMesh.Create(data, idx.Select(i => (uint)i).ToArray(), dynamic: animatable),
                    Skin = skin,
                    Material = mat,
                    Texture = tex.Id,
                    TextureHasAlpha = tex.HasAlpha,
                    TextureIsCutout = tex.Cutout,
                    IsCollisionComponent = comp.IsCollision,
                    IsFragment = comp.IsFragment,
                    ComponentName = comp.Name,
                    Triangles = idx.Count / 3,
                };
                g.Parts.Add(part);
            }
            if (min.X <= max.X) { g.Min = min; g.Max = max; g.HasBounds = true; }
            else { g.Min = model.BoundsMin; g.Max = model.BoundsMax; g.HasBounds = true; }
            return g;
        }

        public void ApplySkin(Matrix4x4[] skin)
        {
            if (!Animatable || skin == null) return;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            int nb = skin.Length;

            var bindMin = new Vector3(float.MaxValue);
            var bindMax = new Vector3(float.MinValue);
            foreach (var p in Parts)
            {
                if (p.Skin == null || p.IsCollisionComponent) continue;
                foreach (var bp in p.Skin.Positions) { bindMin = Vector3.Min(bindMin, bp); bindMax = Vector3.Max(bindMax, bp); }
            }
            float bindRadius = bindMin.X <= bindMax.X ? Math.Max((bindMax - bindMin).Length(), 1f) : 1000f;
            float maxStray = bindRadius * 6f + 200f;
            var bindCenter = bindMin.X <= bindMax.X ? (bindMin + bindMax) * 0.5f : Vector3.Zero;

            foreach (var p in Parts)
            {
                var s = p.Skin;
                if (s == null) continue;
                var d = s.Data;
                bool rigid = s.RigidBone >= 0 && s.RigidBone < nb;
                for (int i = 0; i < s.Positions.Length; i++)
                {
                    Vector3 pos, nrm;
                    int b0 = s.Bone0[i];
                    if (b0 >= 0 && b0 < nb)
                    {
                        int b1 = s.Bone1[i];
                        if (b1 < 0 || b1 >= nb) b1 = b0;
                        float w = s.Weight0[i];
                        ref var m0 = ref skin[b0];
                        ref var m1 = ref skin[b1];
                        pos = Vector3.Transform(s.Positions[i], m0) * w + Vector3.Transform(s.Positions[i], m1) * (1f - w);
                        nrm = Vector3.TransformNormal(s.Normals[i], m0) * w + Vector3.TransformNormal(s.Normals[i], m1) * (1f - w);
                    }
                    else if (rigid)
                    {
                        pos = Vector3.Transform(s.Positions[i], skin[s.RigidBone]);
                        nrm = Vector3.TransformNormal(s.Normals[i], skin[s.RigidBone]);
                    }
                    else
                    {
                        pos = s.Positions[i];
                        nrm = s.Normals[i];
                    }
                    if (float.IsNaN(pos.X) || float.IsInfinity(pos.X) || (pos - bindCenter).Length() > maxStray)
                    {
                        pos = s.Positions[i];
                        nrm = s.Normals[i];
                    }
                    float l = nrm.Length();
                    nrm = l > 1e-6f ? nrm / l : Vector3.UnitY;
                    int o = i * GpuMesh.Stride;
                    d[o] = pos.X; d[o + 1] = pos.Y; d[o + 2] = pos.Z;
                    d[o + 3] = nrm.X; d[o + 4] = nrm.Y; d[o + 5] = nrm.Z;
                    if (!p.IsCollisionComponent)
                    {
                        min = Vector3.Min(min, pos);
                        max = Vector3.Max(max, pos);
                    }
                }
                p.Mesh.UpdateVertices(d);
            }
            if (min.X <= max.X) { Min = min; Max = max; }
        }

        public void Dispose()
        {
            foreach (var p in Parts) p.Mesh.Dispose();
            Parts.Clear();
        }
    }

    public struct TexInfo
    {
        public int Id;
        public bool HasAlpha;
        public bool Cutout;
    }

    /// <summary>GL textures keyed by texture name, decoded through the scene's AssetCache.</summary>
    public sealed class TextureCache : IDisposable
    {
        private readonly Dictionary<string, TexInfo> _tex = new Dictionary<string, TexInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly AssetCache _assets;
        private TexInfo _missing;

        public TextureCache(AssetCache assets) { _assets = assets; }

        public int Count => _tex.Count;

        public TexInfo Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return Missing();
            if (_tex.TryGetValue(name, out var t)) return t;
            var src = _assets.GetTexture(name);
            t = src == null ? Missing() : Upload(src);
            _tex[name] = t;
            return t;
        }

        private TexInfo Missing()
        {
            if (_missing.Id == 0) _missing = Upload(Texture.Missing());
            return _missing;
        }

        public static TexInfo Upload(Texture t)
        {
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, t.Width, t.Height, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, t.Rgba);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            return new TexInfo { Id = id, HasAlpha = t.HasAlpha, Cutout = t.HasCutout };
        }

        public void Dispose()
        {
            foreach (var t in _tex.Values.Select(v => v.Id).Distinct()) if (t != 0) GL.DeleteTexture(t);
            if (_missing.Id != 0) GL.DeleteTexture(_missing.Id);
            _tex.Clear();
            _missing = default;
        }
    }
}
