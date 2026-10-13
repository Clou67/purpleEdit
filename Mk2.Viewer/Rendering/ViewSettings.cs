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
        public bool CollisionOverlay = true;      // draw collision semi-transparent on top of geometry
        public bool ModelCollisionComponents = false; // c_* components inside models
        public bool Fragments = false;               // f_* shatter pieces
        public bool Markers = true;                  // crosses for objects without a model
        public bool Shapes = true;                   // boxes / spheres / paths of logic objects
        public bool MirrorX = true;                  // game is left-handed: mirror X so levels match in-game
        public float Brightness = 1.6f;

        public readonly HashSet<ObjectCategory> HiddenCategories = new HashSet<ObjectCategory>();
        public readonly HashSet<string> HiddenChunks = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        public bool CategoryVisible(ObjectCategory c) => !HiddenCategories.Contains(c);
    }
}
