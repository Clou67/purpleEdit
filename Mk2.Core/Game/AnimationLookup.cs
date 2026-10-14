using Mk2.Formats;

namespace Mk2.Game
{
    /// <summary>Pairs animations with the models they drive.</summary>
    public static class AnimationLookup
    {
        /// <summary>Models that have a skeleton of their own (&lt;name&gt;.mdl next to &lt;name&gt;.anm).</summary>
        public static List<string> ModelsWithSkeletons(GameData game)
        {
            var models = new HashSet<string>(game.AllNames(".mdl").Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            return game.AllNames(".anm").Select(Path.GetFileNameWithoutExtension)
                       .Where(models.Contains)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> AllAnimations(GameData game) =>
            game.AllNames(".anm").Select(Path.GetFileNameWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// Finds the model an animation drives:
        /// 1. a model with the same name (props and characters ship as name.mdl + name.anm);
        /// 2. for cutscene animations (CSA_*, CSE_*), the actor code in the bone names ("z_A002_Head" → A002)
        ///    picks the models named A002_*, preferring one whose anim node count equals the bone count.
        /// Returns null when nothing fits (the skeleton is then shown on its own).
        /// </summary>
        public static string FindModelFor(GameData game, string animName, Animation anim, IList<RkvArchive> packs = null)
        {
            if (game.Exists(animName + ".mdl", packs) && game.Exists(animName + ".mdg", packs)) return animName;
            string code = anim?.ActorCode();
            if (code == null) return null;
            var candidates = game.AllNames(".mdl").Select(Path.GetFileNameWithoutExtension)
                .Where(n => n.StartsWith(code + "_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var c in candidates)
            {
                var mdl = game.Read(c + ".mdl", packs);
                if (mdl != null && mdl.Length > 0x10 && BitConverter.ToUInt16(mdl, 8) == anim.Bones.Count && game.Exists(c + ".mdg", packs)) return c;
            }
            return candidates.FirstOrDefault(c => game.Exists(c + ".mdg", packs));
        }

        /// <summary>Checks that a model and an animation describe the same skeleton.</summary>
        public static string CompatibilityWarning(Model model, Animation anim)
        {
            if (model == null || anim == null) return null;
            int nodes = model.AnimNodePositions.Count;
            if (nodes != anim.Bones.Count)
                return $"model has {nodes} anim nodes but the animation has {anim.Bones.Count} bones — parts may not move correctly";
            for (int i = 0; i < nodes; i++)
                if ((model.AnimNodePositions[i] - anim.Bones[i].BindPosition).Length() > 0.5f)
                    return $"bind position of bone {i} ({anim.Bones[i].Name}) differs between the model and the animation";
            return null;
        }
    }
}
