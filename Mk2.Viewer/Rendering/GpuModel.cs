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
    }

    /// <summary>GPU copy of a model: one part per (component, material).</summary>
    public sealed class GpuModel : IDisposable
    {
        public LoadedModel Source;
        public List<GpuPart> Parts = new List<GpuPart>();
        public Vector3 Min, Max;           // local bounds of visible geometry
        public bool HasBounds;

        public static GpuModel Build(LoadedModel lm, TextureCache textures)
        {
            var g = new GpuModel { Source = lm };
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
                var tex = textures.Get(mat.TextureName);
                var part = new GpuPart
                {
                    Mesh = GpuMesh.Create(data, idx.Select(i => (uint)i).ToArray()),
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
