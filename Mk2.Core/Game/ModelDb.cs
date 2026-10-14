using Mk2.Formats;

namespace Mk2.Game
{
    /// <summary>
    /// Resolves an lc3 object class name to a model file using global.model.bni.
    /// Each section may set "model" (with %s = the section name), "badfile" (animated
    /// model descriptor, same base name as the .mdl) and "alias" (inherit from another section).
    /// Unknown names fall back to a model of the same name.
    /// </summary>
    public sealed class ModelDb
    {
        private readonly Dictionary<string, BniSection> sections = new(StringComparer.OrdinalIgnoreCase);

        public ModelDb(BniFile modelBni)
        {
            if (modelBni == null) return;
            foreach (var section in modelBni.Sections) sections.TryAdd(section.Name, section);
        }

        public bool Has(string name) => sections.ContainsKey(name);
        public BniSection Section(string name) => sections.TryGetValue(name, out var section) ? section : null;

        /// <summary>Field value with alias inheritance.</summary>
        public string Get(string name, string field)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = name;
            while (current != null && seen.Add(current) && sections.TryGetValue(current, out var section))
            {
                var node = section.Field(field);
                if (node != null) return node.JoinedValues;
                current = section.Get("alias");
            }
            return null;
        }

        public IEnumerable<string> AliasChain(string name)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = name;
            while (current != null && seen.Add(current))
            {
                yield return current;
                current = sections.TryGetValue(current, out var section) ? section.Get("alias") : null;
            }
        }

        /// <summary>Candidate model names to try, in priority order.</summary>
        public List<string> ModelCandidates(string className)
        {
            var list = new List<string>();
            void Add(string entry)
            {
                if (string.IsNullOrWhiteSpace(entry) || entry == "none") return;
                entry = entry.Replace("%s", className).Trim();
                int space = entry.IndexOf(' ');
                if (space > 0) entry = entry[..space];
                if (!list.Contains(entry, StringComparer.OrdinalIgnoreCase)) list.Add(entry);
            }
            // The section's own "model" beats inherited ones, so walk the chain in order.
            foreach (var alias in AliasChain(className))
            {
                if (!sections.TryGetValue(alias, out var section)) continue;
                Add(section.Get("model"));
                Add(section.Get("badfile"));
            }
            Add(className);
            return list;
        }

        /// <summary>True when the class is flagged not to draw (e.g. "type global update- draw-").</summary>
        public bool IsHidden(string className)
        {
            var type = Get(className, "type");
            return type != null && type.Contains("draw-", StringComparison.OrdinalIgnoreCase);
        }
    }
}
