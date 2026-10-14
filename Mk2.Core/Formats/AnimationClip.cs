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

        public static AnimSet Parse(byte[] data)
        {
            var reader = new Bin(data);
            if (data.Length < 0x48) throw new InvalidDataException("BBI too short");
            var clips = new AnimSet { ModelName = reader.CStr(0, 0x20) };
            int count = reader.S32(0x40);
            if (count < 0 || count > 4096) throw new InvalidDataException($"BBI clip count {count} looks wrong");
            for (int index = 0; index < count; index++)
            {
                int offset = 0x48 + index * 0x30;
                if (offset + 0x30 > data.Length) break;
                var clip = new AnimClip
                {
                    Name = reader.CStr(reader.S32(offset)),
                    Length = reader.S32(offset + 4),
                    Loop = reader.U16(offset + 8) == 1,
                    Start = reader.U16(offset + 0xA),
                    End = reader.U16(offset + 0xC),
                };
                if (string.IsNullOrEmpty(clip.Name)) clip.Name = "clip" + index;
                if (clip.End < clip.Start) clip.End = clip.Start;
                int evOff = reader.S32(offset + 0x10), evCount = reader.S32(offset + 0x14);
                for (int eventIndex = 0; eventIndex < evCount && eventIndex < 256 && evOff > 0; eventIndex++)
                {
                    int eventOffset = evOff + eventIndex * 0x10;
                    if (eventOffset + 0x10 > data.Length) break;
                    clip.Events.Add(new AnimEvent { Name = reader.CStr(reader.S32(eventOffset)), Type = reader.U16(eventOffset + 4), Frame = reader.U16(eventOffset + 6) });
                }
                clips.Clips.Add(clip);
            }
            return clips;
        }

        /// <summary>A single clip covering the whole timeline, for animations without a .bbi.</summary>
        public static AnimSet WholeTimeline(Animation animation) => new AnimSet
        {
            ModelName = animation.Name,
            Clips = { new AnimClip { Name = "(all frames)", Start = 0, End = Math.Max(0, animation.FrameCount - 1), Length = animation.FrameCount, Loop = true } },
        };
    }
}
