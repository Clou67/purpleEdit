using System.Numerics;
using Mk2.Formats;

namespace Mk2.Game
{
    /// <summary>Moves bind-pose model vertices into a pose.</summary>
    public static class Skinning
    {
        /// <summary>
        /// The bone a component's unskinned geometry follows: the component's rigid bone, then the only bone in
        /// its meshes' anim node lists, then a bone whose name matches the component's name or extra string.
        /// Returns -1 when nothing fits.
        /// </summary>
        public static int ComponentBone(Model model, ModelComponent component, Animation animation)
        {
            int boneCount = animation?.Bones.Count ?? model.AnimNodePositions.Count;
            if (component.RigidBone >= 0 && component.RigidBone < boneCount) return component.RigidBone;

            var listedBones = component.Meshes.SelectMany(model.NodeListBones).Distinct().ToList();
            if (listedBones.Count == 1 && listedBones[0] < boneCount) return listedBones[0];

            if (animation == null) return -1;
            foreach (var label in new[] { component.Extra, component.Name })
            {
                if (string.IsNullOrEmpty(label)) continue;
                var match = animation.Bones.FirstOrDefault(bone => NamesMatch(bone.Name, label));
                if (match != null) return match.Index;
            }
            return -1;
        }

        private static bool NamesMatch(string boneName, string label)
        {
            string bare = label.StartsWith("m_", StringComparison.OrdinalIgnoreCase) ? label.Substring(2) : label;
            return boneName.Equals(label, StringComparison.OrdinalIgnoreCase)
                || boneName.Equals("z_" + bare, StringComparison.OrdinalIgnoreCase)
                || boneName.EndsWith("_" + bare, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Poses one vertex. Skinned vertices blend two bones; anything else follows <paramref name="fallbackBone"/>,
        /// or stays where it is when that is -1.
        /// </summary>
        public static void PoseVertex(Vector3 position, Vector3 normal, bool skinned, int bone0, int bone1, float weight0,
                                      int fallbackBone, Matrix4x4[] skin, out Vector3 posedPosition, out Vector3 posedNormal)
        {
            int boneCount = skin.Length;
            if (skinned && bone0 >= 0 && bone0 < boneCount)
            {
                if (bone1 < 0 || bone1 >= boneCount) bone1 = bone0;
                var first = skin[bone0];
                var second = skin[bone1];
                posedPosition = Vector3.Transform(position, first) * weight0 + Vector3.Transform(position, second) * (1f - weight0);
                posedNormal = Vector3.TransformNormal(normal, first) * weight0 + Vector3.TransformNormal(normal, second) * (1f - weight0);
            }
            else if (fallbackBone >= 0 && fallbackBone < boneCount)
            {
                posedPosition = Vector3.Transform(position, skin[fallbackBone]);
                posedNormal = Vector3.TransformNormal(normal, skin[fallbackBone]);
            }
            else
            {
                posedPosition = position;
                posedNormal = normal;
            }
        }
    }
}
