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
        public bool Transparent => Material.Blend != BlendMode.Opaque || VertexAlpha || (TextureHasAlpha && !Material.AlphaTest && !TextureIsCutout);
        /// <summary>Some vertices fade out (overlay edges blending into the ground below).</summary>
        public bool VertexAlpha;
        public bool TextureIsCutout;
        public SkinSource Skin;
    }

    /// <summary>Per-vertex bind pose and bone data for one part, and the interleaved buffer it is skinned into.</summary>
    public sealed class SkinSource
    {
        public Vector3[] Positions, Normals;
        public bool[] Skinned;
        public byte[] Bone0, Bone1;
        public float[] Weight0;
        public int[] FallbackBone;
        public float[] Data;
    }

    /// <summary>GPU copy of a model: one part per (component, material).</summary>
    public sealed class GpuModel : IDisposable
    {
        public LoadedModel Source;
        public List<GpuPart> Parts = new List<GpuPart>();
        public Vector3 Min, Max;           // local bounds of visible geometry
        public bool HasBounds;

        public bool Animatable;

        /// <summary>Uploads a model. Passing an animation keeps the skin data needed by <see cref="ApplySkin"/>.</summary>
        public static GpuModel Build(LoadedModel lm, TextureCache textures, Animation animation = null)
        {
            bool animatable = animation != null;
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
                if (animatable) skin = BuildSkin(model, comp, grp, data, animation);
                var tex = textures.Get(mat.TextureName);
                var part = new GpuPart
                {
                    Mesh = GpuMesh.Create(data, idx.Select(i => (uint)i).ToArray(), dynamic: animatable),
                    Skin = skin,
                    Material = mat,
                    Texture = tex.Id,
                    TextureHasAlpha = tex.HasAlpha,
                    TextureIsCutout = tex.Cutout,
                    VertexAlpha = verts.Any(vertex => vertex.Color.W < 0.95f),
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

        private static SkinSource BuildSkin(Model model, ModelComponent component, IEnumerable<ModelMesh> meshes, float[] data, Animation animation)
        {
            int componentBone = Skinning.ComponentBone(model, component, animation);
            var positions = new List<Vector3>();
            var skinned = new List<bool>();
            var bone0 = new List<byte>();
            var bone1 = new List<byte>();
            var weight0 = new List<float>();
            var fallbackBone = new List<int>();
            foreach (var mesh in meshes)
            {
                var listedBones = model.NodeListBones(mesh);
                int meshFallback = componentBone >= 0 ? componentBone : listedBones.Count > 0 ? listedBones[0] : -1;
                foreach (var strip in mesh.Strips)
                    foreach (var vertex in strip.Vertices)
                    {
                        positions.Add(vertex.Position);
                        skinned.Add(strip.Skinned);
                        bone0.Add(vertex.Bone0);
                        bone1.Add(vertex.Bone1);
                        weight0.Add(vertex.Weight0);
                        fallbackBone.Add(meshFallback);
                    }
            }
            var normals = new Vector3[positions.Count];
            for (int index = 0; index < normals.Length; index++)
            {
                int offset = index * GpuMesh.Stride;
                normals[index] = new Vector3(data[offset + 3], data[offset + 4], data[offset + 5]);
            }
            return new SkinSource
            {
                Positions = positions.ToArray(),
                Normals = normals,
                Skinned = skinned.ToArray(),
                Bone0 = bone0.ToArray(),
                Bone1 = bone1.ToArray(),
                Weight0 = weight0.ToArray(),
                FallbackBone = fallbackBone.ToArray(),
                Data = data,
            };
        }

        public void ApplySkin(Matrix4x4[] skinMatrices)
        {
            if (!Animatable || skinMatrices == null) return;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var part in Parts)
            {
                var skin = part.Skin;
                if (skin == null) continue;
                var data = skin.Data;
                for (int index = 0; index < skin.Positions.Length; index++)
                {
                    Skinning.PoseVertex(skin.Positions[index], skin.Normals[index], skin.Skinned[index], skin.Bone0[index], skin.Bone1[index],
                                        skin.Weight0[index], skin.FallbackBone[index], skinMatrices, out var position, out var normal);
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                    {
                        position = skin.Positions[index];
                        normal = skin.Normals[index];
                    }
                    float length = normal.Length();
                    normal = length > 1e-6f ? normal / length : Vector3.UnitY;
                    int offset = index * GpuMesh.Stride;
                    data[offset] = position.X; data[offset + 1] = position.Y; data[offset + 2] = position.Z;
                    data[offset + 3] = normal.X; data[offset + 4] = normal.Y; data[offset + 5] = normal.Z;
                    if (!part.IsCollisionComponent)
                    {
                        min = Vector3.Min(min, position);
                        max = Vector3.Max(max, position);
                    }
                }
                part.Mesh.UpdateVertices(data);
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
