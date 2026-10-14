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

        private readonly AssetCache assetCache;
        private readonly MemoryStream binary = new();
        private readonly JsonArray accessors = new(), views = new(), meshes = new(), nodes = new(),
            materials = new(), textures = new(), images = new(), rootNodes = new();
        private readonly Dictionary<LoadedModel, int> meshIndices = new();
        private readonly Dictionary<string, int> materialIndices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> textureIndices = new(StringComparer.OrdinalIgnoreCase);

        public GlbExporter(AssetCache assets) { assetCache = assets; }

        public void AddModel(LoadedModel loadedModel, Matrix4x4 world, string nodeName, int? parent = null)
        {
            int mesh = MeshFor(loadedModel);
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
            int nodeIndex = nodes.Count;
            nodes.Add(node);
            if (parent.HasValue) ((JsonArray)nodes[parent.Value]!["children"]!).Add(nodeIndex);
            else rootNodes.Add(nodeIndex);
            return nodeIndex;
        }

        public void AddCollision(Collision collision, Matrix4x4 world, string name, int? parent = null)
        {
            if (collision == null || collision.Triangles.Count == 0) return;
            var pos = collision.Vertices.ToArray();
            var indices = new List<int>();
            foreach (var triangle in collision.Triangles) { indices.Add(triangle.First); indices.Add(triangle.Second); indices.Add(triangle.Third); }
            var cols = collision.VertexColors.Select(packed => new Vector4((packed & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, ((packed >> 16) & 0xFF) / 255f, 1f)).ToArray();
            int materialIndex = CollisionMaterial();
            var prim = new JsonObject
            {
                ["attributes"] = new JsonObject
                {
                    ["POSITION"] = Accessor(pos),
                    ["COLOR_0"] = Accessor(cols),
                },
                ["indices"] = Indices(indices),
                ["material"] = materialIndex,
            };
            int mesh = meshes.Count;
            meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = new JsonArray(prim) });
            var node = new JsonObject { ["name"] = name, ["mesh"] = mesh };
            if (!world.IsIdentity) node["matrix"] = MatrixJson(world);
            AddNode(node, parent);
        }

        public void AddScene(LevelScene scene, bool objects = true)
        {
            foreach (var chunk in scene.Chunks)
            {
                int group = AddGroup(chunk.Def.Name);
                foreach (var (overlay, loadedModel) in chunk.Overlays)
                    if (loadedModel != null) AddModel(loadedModel, Matrix4x4.Identity, overlay.Name, group);
                if (chunk.EnvDome != null) AddModel(chunk.EnvDome, Matrix4x4.Identity, "envDome_" + chunk.EnvDome.Name, group);
                if (IncludeGroundCollision)
                    foreach (var (name, collision) in chunk.Grounds) AddCollision(collision, Matrix4x4.Identity, "collision_" + name, group);
                if (!objects) continue;
                int objectGroup = AddGroup(chunk.Def.Name + "_objects", group);
                foreach (var levelObject in chunk.Objects)
                    if (chunk.ObjectModels.TryGetValue(levelObject, out var loadedModel))
                        AddModel(loadedModel, levelObject.ModelMatrix, $"{levelObject.Index:0000}_{levelObject.DisplayName}", objectGroup);
            }
        }

        private int CollisionMaterial()
        {
            if (materialIndices.TryGetValue("<collision>", out var index)) return index;
            index = materials.Count;
            materials.Add(new JsonObject
            {
                ["name"] = "collision",
                ["doubleSided"] = true,
                ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(1, 1, 1, 1), ["metallicFactor"] = 0, ["roughnessFactor"] = 1 },
            });
            materialIndices["<collision>"] = index;
            return index;
        }

        private int MeshFor(LoadedModel loadedModel)
        {
            if (loadedModel?.Model == null) return -1;
            if (meshIndices.TryGetValue(loadedModel, out var existing)) return existing;
            var prims = new JsonArray();
            for (int materialIndex = 0; materialIndex < loadedModel.Model.MaterialNames.Count; materialIndex++)
            {
                var vertices = new List<ModelVertex>();
                var indices = new List<int>();
                foreach (var mesh in loadedModel.Model.Meshes.Where(candidate => candidate.MaterialIndex == materialIndex))
                {
                    var comp = loadedModel.Model.Components[mesh.ComponentIndex];
                    if (comp.IsCollision && !IncludeCollisionComponents) continue;
                    if (comp.IsFragment && !IncludeFragments) continue;
                    mesh.Triangulate(vertices, indices);
                }
                if (indices.Count == 0) continue;
                var material = materialIndex < loadedModel.Materials.Count ? loadedModel.Materials[materialIndex] : Material.Default(loadedModel.Model.MaterialNames[materialIndex]);
                var prim = new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = Accessor(vertices.Select(vertex => vertex.Position).ToArray()),
                        ["NORMAL"] = Accessor(vertices.Select(vertex => vertex.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(vertex.Normal) : Vector3.UnitY).ToArray()),
                        ["TEXCOORD_0"] = Accessor(vertices.Select(vertex => vertex.UV).ToArray()),
                        ["COLOR_0"] = Accessor(vertices.Select(vertex => Vector4.Min(vertex.Color, Vector4.One)).ToArray()),
                    },
                    ["indices"] = Indices(indices),
                    ["material"] = MaterialFor(material),
                };
                prims.Add(prim);
            }
            int index = -1;
            if (prims.Count > 0)
            {
                index = meshes.Count;
                meshes.Add(new JsonObject { ["name"] = loadedModel.Name, ["primitives"] = prims });
            }
            meshIndices[loadedModel] = index;
            return index;
        }

        private int MaterialFor(Material material)
        {
            if (materialIndices.TryGetValue(material.Name, out var index)) return index;
            var texture = assetCache.GetTexture(material.TextureName);
            var pbr = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 1 };
            if (texture != null) pbr["baseColorTexture"] = new JsonObject { ["index"] = TextureFor(texture) };
            var materialJson = new JsonObject { ["name"] = material.Name, ["pbrMetallicRoughness"] = pbr, ["doubleSided"] = true };
            if (material.Blend != BlendMode.Opaque || (texture != null && texture.HasAlpha && !texture.HasCutout && !material.AlphaTest))
                materialJson["alphaMode"] = "BLEND";
            else if (material.AlphaTest || (texture != null && texture.HasCutout))
            {
                materialJson["alphaMode"] = "MASK";
                materialJson["alphaCutoff"] = material.AlphaRef > 0 ? material.AlphaRef : 0.5f;
            }
            if (material.Blend == BlendMode.Additive) materialJson["extras"] = new JsonObject { ["mk2Blend"] = "additive" };
            index = materials.Count;
            materials.Add(materialJson);
            materialIndices[material.Name] = index;
            return index;
        }

        private int TextureFor(Texture texture)
        {
            if (textureIndices.TryGetValue(texture.Name, out var index)) return index;
            var png = Png.Encode(texture.Rgba, texture.Width, texture.Height);
            int view = View(png, null);
            images.Add(new JsonObject { ["name"] = texture.Name, ["mimeType"] = "image/png", ["bufferView"] = view });
            index = textures.Count;
            textures.Add(new JsonObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
            textureIndices[texture.Name] = index;
            return index;
        }

        private int View(byte[] data, int? target)
        {
            while (binary.Length % 4 != 0) binary.WriteByte(0);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = binary.Length, ["byteLength"] = data.Length };
            if (target.HasValue) view["target"] = target.Value;
            binary.Write(data);
            views.Add(view);
            return views.Count - 1;
        }

        private int Accessor(Vector3[] values)
        {
            var bytes = new byte[values.Length * 12];
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (int index = 0; index < values.Length; index++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 12), values[index].X);
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 12 + 4), values[index].Y);
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 12 + 8), values[index].Z);
                min = Vector3.Min(min, values[index]); max = Vector3.Max(max, values[index]);
            }
            int view = View(bytes, 34962);
            accessors.Add(new JsonObject
            {
                ["bufferView"] = view, ["componentType"] = 5126, ["count"] = values.Length, ["type"] = "VEC3",
                ["min"] = new JsonArray(min.X, min.Y, min.Z), ["max"] = new JsonArray(max.X, max.Y, max.Z),
            });
            return accessors.Count - 1;
        }

        private int Accessor(Vector2[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (int index = 0; index < values.Length; index++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 8), values[index].X);
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 8 + 4), values[index].Y);
            }
            int view = View(bytes, 34962);
            accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = values.Length, ["type"] = "VEC2" });
            return accessors.Count - 1;
        }

        private int Accessor(Vector4[] values)
        {
            var bytes = new byte[values.Length * 16];
            for (int index = 0; index < values.Length; index++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 16), values[index].X);
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 16 + 4), values[index].Y);
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 16 + 8), values[index].Z);
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 16 + 12), values[index].W);
            }
            int view = View(bytes, 34962);
            accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = values.Length, ["type"] = "VEC4" });
            return accessors.Count - 1;
        }

        private int Indices(List<int> indices)
        {
            var bytes = new byte[indices.Count * 4];
            for (int index = 0; index < indices.Count; index++) BitConverter.TryWriteBytes(bytes.AsSpan(index * 4), (uint)indices[index]);
            int view = View(bytes, 34963);
            accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5125, ["count"] = indices.Count, ["type"] = "SCALAR" });
            return accessors.Count - 1;
        }

        private static JsonArray MatrixJson(Matrix4x4 matrix) => new(
            matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44);

        public void Save(string path)
        {
            while (binary.Length % 4 != 0) binary.WriteByte(0);
            var root = new JsonObject
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "purpleEdit" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = rootNodes }),
                ["nodes"] = nodes,
                ["meshes"] = meshes,
                ["accessors"] = accessors,
                ["bufferViews"] = views,
                ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = binary.Length }),
                ["samplers"] = new JsonArray(new JsonObject { ["wrapS"] = 10497, ["wrapT"] = 10497, ["magFilter"] = 9729, ["minFilter"] = 9987 }),
            };
            if (materials.Count > 0) root["materials"] = materials;
            if (textures.Count > 0) { root["textures"] = textures; root["images"] = images; }
            var json = Encoding.UTF8.GetBytes(root.ToJsonString());
            int jsonPad = (4 - json.Length % 4) % 4;
            var bin = binary.ToArray();
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(0x46546C67); writer.Write(2);
            writer.Write(12 + 8 + json.Length + jsonPad + 8 + bin.Length);
            writer.Write(json.Length + jsonPad); writer.Write(0x4E4F534A);
            writer.Write(json); for (int index = 0; index < jsonPad; index++) writer.Write((byte)' ');
            writer.Write(bin.Length); writer.Write(0x004E4942);
            writer.Write(bin);
        }
    }
}
