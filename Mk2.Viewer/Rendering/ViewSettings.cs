using System.Collections.Generic;
using Mk2.Game;

namespace Mk2.Viewer.Rendering
{
    public sealed class ViewSettings
    {
        public bool Overlays = true;
        public bool Objects = true;
        public bool EnvDome = true;
        public bool Textures = true;
        public bool VertexColors = true;
        public bool Lighting = false;
        public bool Wireframe = false;
        public bool CullBackfaces = false;
        public bool Fog = false;
        public bool Transparency = true;
        public bool GroundCollision = false;
        public bool ObjectCollision = false;
        public bool CollisionOverlay = false;     // draw collision semi-transparent on top of geometry (off = solid)
        public bool ModelCollisionComponents = false; // c_* components inside models
        public bool Fragments = false;               // f_* shatter pieces
        public bool Markers = true;                  // crosses for objects without a model
        public bool Shapes = true;                   // boxes / spheres / paths of logic objects
        public bool MirrorX = true;                  // game is left-handed: mirror X so levels match in-game
        public float Brightness = 1.6f;
        public bool Skeleton = true;                 // bone lines for the model in the Animations / Skeletons tabs
        public bool AnimatedMesh = true;             // the posed mesh itself (off = skeleton only)

        public readonly HashSet<ObjectCategory> HiddenCategories = new HashSet<ObjectCategory>();
        public readonly HashSet<string> HiddenChunks = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        public bool CategoryVisible(ObjectCategory c) => !HiddenCategories.Contains(c);

        // ---- per-chunk / per-item visibility from the Scene tree (tuple keys: no per-frame allocations)
        /// <summary>Hidden groups per chunk: "overlays", "objects", "grounds", "dome".</summary>
        public readonly HashSet<(string chunk, string group)> HiddenGroups = new HashSet<(string, string)>();
        public readonly HashSet<(string chunk, ObjectCategory cat)> HiddenChunkCategories = new HashSet<(string, ObjectCategory)>();
        /// <summary>Individually hidden overlays / ground collision meshes.</summary>
        public readonly HashSet<(string chunk, string name)> HiddenItems = new HashSet<(string, string)>();
        public readonly HashSet<LevelObject> HiddenObjects = new HashSet<LevelObject>();

        public bool GroupHidden(string chunk, string group) => HiddenGroups.Contains((chunk, group));

        public bool OverlayVisible(string chunk, string name) =>
            Overlays && !HiddenChunks.Contains(chunk) && !HiddenGroups.Contains((chunk, "overlays")) && !HiddenItems.Contains((chunk, name));

        public bool DomeVisible(string chunk) => EnvDome && !HiddenChunks.Contains(chunk) && !HiddenGroups.Contains((chunk, "dome"));

        public bool GroundVisible(string chunk, string name) =>
            GroundCollision && !HiddenChunks.Contains(chunk) && !HiddenGroups.Contains((chunk, "grounds")) && !HiddenItems.Contains((chunk, name));

        public bool CategoryBatchVisible(string chunk, ObjectCategory c) =>
            Objects && !HiddenChunks.Contains(chunk) && !HiddenGroups.Contains((chunk, "objects"))
            && CategoryVisible(c) && !HiddenChunkCategories.Contains((chunk, c));

        public bool ObjectVisible(LevelObject o) => CategoryBatchVisible(o.ChunkName, o.Category) && !HiddenObjects.Contains(o);

        public void ClearSceneVisibility()
        {
            HiddenChunks.Clear(); HiddenGroups.Clear(); HiddenChunkCategories.Clear(); HiddenItems.Clear(); HiddenObjects.Clear();
        }
    }
}
