using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mk2.Formats;
using Mk2.Game;

namespace Mk2.Export
{
    /// <summary>
    /// Writes models / whole levels to a single self-contained binary glTF (.glb) with
    /// embedded PNG textures, vertex colours and per-object node transforms.
    /// </summary>
    public sealed class GlbExporter
    {
        public bool IncludeCollisionComponents = false;
        public bool IncludeFragments = false;
        public bool IncludeGroundCollision = false;

        private readonly AssetCache _assets;
        private readonly MemoryStream _bin = new();
        private readonly JsonArray _accessors = new(), _views = new(), _meshes = new(), _nodes = new(),
            _materials = new(), _textures = new(), _images = new(), _rootNodes = new();
        private readonly Dictionary<LoadedModel, int> _meshIndex = new();
        private readonly Dictionary<string, int> _materialIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _textureIndex = new(StringComparer.OrdinalIgnoreCase);

        public GlbExporter(AssetCache assets) { _assets = assets; }

        public void AddModel(LoadedModel m, Matrix4x4 world, string nodeName, int? parent = null)
        {
            int mesh = MeshFor(m);
            if (mesh < 0) return;
            var node = new JsonObject { ["name"] = nodeName, ["mesh"] = mesh };
            if (!world.IsIdentity) node["matrix"] = MatrixJson(world);
            AddNode(node, parent);
        }

        public int AddGroup(string name, int? parent = null)
        {
            var node = new JsonObject { ["name"] = name, ["children"] = new JsonArray() };
            return AddNode(node, parent);
        }

        private int AddNode(JsonObject node, int? parent)
        {
            int idx = _nodes.Count;
            _nodes.Add(node);
            if (parent.HasValue) ((JsonArray)_nodes[parent.Value]!["children"]!).Add(idx);
            else _rootNodes.Add(idx);
            return idx;
        }

        public void AddCollision(Collision c, Matrix4x4 world, string name, int? parent = null)
        {
            if (c == null || c.Triangles.Count == 0) return;
            var pos = c.Vertices.ToArray();
            var idx = new List<int>();
            foreach (var t in c.Triangles) { idx.Add(t.A); idx.Add(t.B); idx.Add(t.C); }
            var cols = c.VertexColors.Select(u => new Vector4((u & 0xFF) / 255f, ((u >> 8) & 0xFF) / 255f, ((u >> 16) & 0xFF) / 255f, 1f)).ToArray();
            int mat = CollisionMaterial();
            var prim = new JsonObject
            {
                ["attributes"] = new JsonObject
                {
                    ["POSITION"] = Accessor(pos),
                    ["COLOR_0"] = Accessor(cols),
                },
                ["indices"] = Indices(idx),
                ["material"] = mat,
            };
            int mesh = _meshes.Count;
            _meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = new JsonArray(prim) });
            var node = new JsonObject { ["name"] = name, ["mesh"] = mesh };
            if (!world.IsIdentity) node["matrix"] = MatrixJson(world);
            AddNode(node, parent);
        }

        public void AddScene(LevelScene scene, bool objects = true)
        {
            foreach (var ch in scene.Chunks)
            {
                int g = AddGroup(ch.Def.Name);
                foreach (var (ov, m) in ch.Overlays)
                    if (m != null) AddModel(m, Matrix4x4.Identity, ov.Name, g);
                if (ch.EnvDome != null) AddModel(ch.EnvDome, Matrix4x4.Identity, "envDome_" + ch.EnvDome.Name, g);
                if (IncludeGroundCollision)
                    foreach (var (n, c) in ch.Grounds) AddCollision(c, Matrix4x4.Identity, "collision_" + n, g);
                if (!objects) continue;
                int og = AddGroup(ch.Def.Name + "_objects", g);
                foreach (var o in ch.Objects)
                    if (ch.ObjectModels.TryGetValue(o, out var m))
                        AddModel(m, o.ModelMatrix, $"{o.Index:0000}_{o.DisplayName}", og);
            }
        }

        private int CollisionMaterial()
        {
            if (_materialIndex.TryGetValue("<collision>", out var i)) return i;
            i = _materials.Count;
            _materials.Add(new JsonObject
            {
                ["name"] = "collision",
                ["doubleSided"] = true,
                ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(1, 1, 1, 1), ["metallicFactor"] = 0, ["roughnessFactor"] = 1 },
            });
            _materialIndex["<collision>"] = i;
            return i;
        }

        private int MeshFor(LoadedModel m)
        {
            if (m?.Model == null) return -1;
            if (_meshIndex.TryGetValue(m, out var existing)) return existing;
            var prims = new JsonArray();
            for (int mi = 0; mi < m.Model.MaterialNames.Count; mi++)
            {
                var verts = new List<ModelVertex>();
                var idx = new List<int>();
                foreach (var mesh in m.Model.Meshes.Where(x => x.MaterialIndex == mi))
                {
                    var comp = m.Model.Components[mesh.ComponentIndex];
                    if (comp.IsCollision && !IncludeCollisionComponents) continue;
                    if (comp.IsFragment && !IncludeFragments) continue;
                    mesh.Triangulate(verts, idx);
                }
                if (idx.Count == 0) continue;
                var mat = mi < m.Materials.Count ? m.Materials[mi] : Material.Default(m.Model.MaterialNames[mi]);
                var prim = new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = Accessor(verts.Select(v => v.Position).ToArray()),
                        ["NORMAL"] = Accessor(verts.Select(v => v.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(v.Normal) : Vector3.UnitY).ToArray()),
                        ["TEXCOORD_0"] = Accessor(verts.Select(v => v.UV).ToArray()),
                        ["COLOR_0"] = Accessor(verts.Select(v => Vector4.Min(v.Color, Vector4.One)).ToArray()),
                    },
                    ["indices"] = Indices(idx),
                    ["material"] = MaterialFor(mat),
                };
                prims.Add(prim);
            }
            int index = -1;
            if (prims.Count > 0)
            {
                index = _meshes.Count;
                _meshes.Add(new JsonObject { ["name"] = m.Name, ["primitives"] = prims });
            }
            _meshIndex[m] = index;
            return index;
        }

        private int MaterialFor(Material mat)
        {
            if (_materialIndex.TryGetValue(mat.Name, out var i)) return i;
            var tex = _assets.GetTexture(mat.TextureName);
            var pbr = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 1 };
            if (tex != null) pbr["baseColorTexture"] = new JsonObject { ["index"] = TextureFor(tex) };
            var jm = new JsonObject { ["name"] = mat.Name, ["pbrMetallicRoughness"] = pbr, ["doubleSided"] = true };
            if (mat.Blend != BlendMode.Opaque || (tex != null && tex.HasAlpha && !tex.HasCutout && !mat.AlphaTest))
                jm["alphaMode"] = "BLEND";
            else if (mat.AlphaTest || (tex != null && tex.HasCutout))
            {
                jm["alphaMode"] = "MASK";
                jm["alphaCutoff"] = mat.AlphaRef > 0 ? mat.AlphaRef : 0.5f;
            }
            if (mat.Blend == BlendMode.Additive) jm["extras"] = new JsonObject { ["mk2Blend"] = "additive" };
            i = _materials.Count;
            _materials.Add(jm);
            _materialIndex[mat.Name] = i;
            return i;
        }

        private int TextureFor(Texture t)
        {
            if (_textureIndex.TryGetValue(t.Name, out var i)) return i;
            var png = Png.Encode(t.Rgba, t.Width, t.Height);
            int view = View(png, null);
            _images.Add(new JsonObject { ["name"] = t.Name, ["mimeType"] = "image/png", ["bufferView"] = view });
            i = _textures.Count;
            _textures.Add(new JsonObject { ["source"] = _images.Count - 1, ["sampler"] = 0 });
            _textureIndex[t.Name] = i;
            return i;
        }

        private int View(byte[] data, int? target)
        {
            while (_bin.Length % 4 != 0) _bin.WriteByte(0);
            var v = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = data.Length };
            if (target.HasValue) v["target"] = target.Value;
            _bin.Write(data);
            _views.Add(v);
            return _views.Count - 1;
        }

        private int Accessor(Vector3[] v)
        {
            var b = new byte[v.Length * 12];
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int i = 0; i < v.Length; i++)
            {
                BitConverter.TryWriteBytes(b.AsSpan(i * 12), v[i].X);
                BitConverter.TryWriteBytes(b.AsSpan(i * 12 + 4), v[i].Y);
                BitConverter.TryWriteBytes(b.AsSpan(i * 12 + 8), v[i].Z);
                mn = Vector3.Min(mn, v[i]); mx = Vector3.Max(mx, v[i]);
            }
            int view = View(b, 34962);
            _accessors.Add(new JsonObject
            {
                ["bufferView"] = view, ["componentType"] = 5126, ["count"] = v.Length, ["type"] = "VEC3",
                ["min"] = new JsonArray(mn.X, mn.Y, mn.Z), ["max"] = new JsonArray(mx.X, mx.Y, mx.Z),
            });
            return _accessors.Count - 1;
        }

        private int Accessor(Vector2[] v)
        {
            var b = new byte[v.Length * 8];
            for (int i = 0; i < v.Length; i++)
            {
                BitConverter.TryWriteBytes(b.AsSpan(i * 8), v[i].X);
                BitConverter.TryWriteBytes(b.AsSpan(i * 8 + 4), v[i].Y);
            }
            int view = View(b, 34962);
            _accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = v.Length, ["type"] = "VEC2" });
            return _accessors.Count - 1;
        }

        private int Accessor(Vector4[] v)
        {
            var b = new byte[v.Length * 16];
            for (int i = 0; i < v.Length; i++)
            {
                BitConverter.TryWriteBytes(b.AsSpan(i * 16), v[i].X);
                BitConverter.TryWriteBytes(b.AsSpan(i * 16 + 4), v[i].Y);
                BitConverter.TryWriteBytes(b.AsSpan(i * 16 + 8), v[i].Z);
                BitConverter.TryWriteBytes(b.AsSpan(i * 16 + 12), v[i].W);
            }
            int view = View(b, 34962);
            _accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = v.Length, ["type"] = "VEC4" });
            return _accessors.Count - 1;
        }

        private int Indices(List<int> idx)
        {
            var b = new byte[idx.Count * 4];
            for (int i = 0; i < idx.Count; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), (uint)idx[i]);
            int view = View(b, 34963);
            _accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5125, ["count"] = idx.Count, ["type"] = "SCALAR" });
            return _accessors.Count - 1;
        }

        private static JsonArray MatrixJson(Matrix4x4 m) => new(
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

        public void Save(string path)
        {
            while (_bin.Length % 4 != 0) _bin.WriteByte(0);
            var root = new JsonObject
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "purpleEdit" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = _rootNodes }),
                ["nodes"] = _nodes,
                ["meshes"] = _meshes,
                ["accessors"] = _accessors,
                ["bufferViews"] = _views,
                ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _bin.Length }),
                ["samplers"] = new JsonArray(new JsonObject { ["wrapS"] = 10497, ["wrapT"] = 10497, ["magFilter"] = 9729, ["minFilter"] = 9987 }),
            };
            if (_materials.Count > 0) root["materials"] = _materials;
            if (_textures.Count > 0) { root["textures"] = _textures; root["images"] = _images; }
            var json = Encoding.UTF8.GetBytes(root.ToJsonString());
            int jsonPad = (4 - json.Length % 4) % 4;
            var bin = _bin.ToArray();
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            w.Write(0x46546C67); w.Write(2);
            w.Write(12 + 8 + json.Length + jsonPad + 8 + bin.Length);
            w.Write(json.Length + jsonPad); w.Write(0x4E4F534A);
            w.Write(json); for (int i = 0; i < jsonPad; i++) w.Write((byte)' ');
            w.Write(bin.Length); w.Write(0x004E4942);
            w.Write(bin);
        }
    }
}
