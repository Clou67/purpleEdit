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
        private readonly Dictionary<string, BniSection> _sections = new(StringComparer.OrdinalIgnoreCase);

        public ModelDb(BniFile modelBni)
        {
            if (modelBni == null) return;
            foreach (var s in modelBni.Sections) _sections.TryAdd(s.Name, s);
        }

        public bool Has(string name) => _sections.ContainsKey(name);
        public BniSection Section(string name) => _sections.TryGetValue(name, out var s) ? s : null;

        /// <summary>Field value with alias inheritance.</summary>
        public string Get(string name, string field)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cur = name;
            while (cur != null && seen.Add(cur) && _sections.TryGetValue(cur, out var s))
            {
                var v = s.Field(field);
                if (v != null) return v.JoinedValues;
                cur = s.Get("alias");
            }
            return null;
        }

        public IEnumerable<string> AliasChain(string name)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cur = name;
            while (cur != null && seen.Add(cur))
            {
                yield return cur;
                cur = _sections.TryGetValue(cur, out var s) ? s.Get("alias") : null;
            }
        }

        /// <summary>Candidate model names to try, in priority order.</summary>
        public List<string> ModelCandidates(string className)
        {
            var list = new List<string>();
            void Add(string s)
            {
                if (string.IsNullOrWhiteSpace(s) || s == "none") return;
                s = s.Replace("%s", className).Trim();
                int sp = s.IndexOf(' ');
                if (sp > 0) s = s[..sp];
                if (!list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
            }
            // The section's own "model" beats inherited ones, so walk the chain in order.
            foreach (var n in AliasChain(className))
            {
                if (!_sections.TryGetValue(n, out var s)) continue;
                Add(s.Get("model"));
                Add(s.Get("badfile"));
            }
            Add(className);
            return list;
        }

        /// <summary>True when the class is flagged not to draw (e.g. "type global update- draw-").</summary>
        public bool IsHidden(string className)
        {
            var t = Get(className, "type");
            return t != null && t.Contains("draw-", StringComparison.OrdinalIgnoreCase);
        }
    }
}
