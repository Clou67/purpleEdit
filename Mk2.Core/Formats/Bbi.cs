using Mk2.IO;

namespace Mk2.Formats
{
    public sealed class AnimEvent
    {
        public string Name;     // sound name (CavesDoorMove) or attach/effect point (LeftHand)
        public int Type;        // 1 = sound, 0 = other (?)
        public int Frame;       // absolute frame on the ANM timeline
        public override string ToString() => $"{Frame}: {Name}";
    }

    public sealed class AnimClip
    {
        public string Name;
        public int Start, End;  // inclusive frame range on the ANM timeline
        public int Length;      // u32 at +4, usually End − Start
        public bool Loop;
        public List<AnimEvent> Events = new();
        public override string ToString() => Name;
    }

    /// <summary>
    /// BBI: the clip list for one animated model (the MK2 version of TY's .bad files).
    ///   0x00 char[] model name (followed by other strings)
    ///   0x40 u32 clip count, 0x48 clips, 0x30 bytes each:
    ///     +0x00 u32 name offset  +0x04 u32 length  +0x08 u16 loop (1) / stop (0)  +0x0A u16 start  +0x0C u16 end
    ///     +0x10 4 × (u32 offset, u32 count) sub-tables; the first lists frame events, 0x10 bytes each:
    ///           u32 name offset, u16 type (1 = sound), u16 frame, u32 −1, u32 frame
    ///     The other three sub-tables have always been empty so far.
    /// </summary>
    public sealed class AnimSet
    {
        public string ModelName;
        public List<AnimClip> Clips = new();

        public static AnimSet Parse(byte[] d)
        {
            var b = new Bin(d);
            if (d.Length < 0x48) throw new InvalidDataException("BBI too short");
            var s = new AnimSet { ModelName = b.CStr(0, 0x20) };
            int count = b.S32(0x40);
            if (count < 0 || count > 4096) throw new InvalidDataException($"BBI clip count {count} looks wrong");
            for (int i = 0; i < count; i++)
            {
                int o = 0x48 + i * 0x30;
                if (o + 0x30 > d.Length) break;
                var c = new AnimClip
                {
                    Name = b.CStr(b.S32(o)),
                    Length = b.S32(o + 4),
                    Loop = b.U16(o + 8) == 1,
                    Start = b.U16(o + 0xA),
                    End = b.U16(o + 0xC),
                };
                if (string.IsNullOrEmpty(c.Name)) c.Name = "clip" + i;
                if (c.End < c.Start) c.End = c.Start;
                int evOff = b.S32(o + 0x10), evCount = b.S32(o + 0x14);
                for (int k = 0; k < evCount && k < 256 && evOff > 0; k++)
                {
                    int e = evOff + k * 0x10;
                    if (e + 0x10 > d.Length) break;
                    c.Events.Add(new AnimEvent { Name = b.CStr(b.S32(e)), Type = b.U16(e + 4), Frame = b.U16(e + 6) });
                }
                s.Clips.Add(c);
            }
            return s;
        }

        /// <summary>A single clip covering the whole timeline, for animations without a .bbi.</summary>
        public static AnimSet WholeTimeline(Animation a) => new AnimSet
        {
            ModelName = a.Name,
            Clips = { new AnimClip { Name = "(all frames)", Start = 0, End = Math.Max(0, a.FrameCount - 1), Length = a.FrameCount, Loop = true } },
        };
    }
}
