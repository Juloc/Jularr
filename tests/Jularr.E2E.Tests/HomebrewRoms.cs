using System.Buffers.Binary;

namespace Jularr.E2E.Tests
{
    // Tiny homebrew written here, so the browser player is tested with nothing but our own bytes: a few ARM instructions that colour the screens and halt.
    internal static class HomebrewRoms
    {
        private const uint StoreHalf = 0xE1C010B0;     // strh r1, [r0]
        private const uint StoreWord = 0xE5801000;     // str r1, [r0]
        private const uint StoreWordNext = 0xE4801004; // str r1, [r0], #4
        private const uint CompareEnd = 0xE1500002;    // cmp r0, r2
        private const uint LoadHalf = 0xE1D010B0;      // ldrh r1, [r0]
        private const uint StoreHalfShared = 0xE1C210B0; // strh r1, [r2]
        private const uint TestTouch = 0xE3110040;     // tst r1, #0x40
        private const uint StoreRedIfTouched = 0x01C350B0; // strheq r5, [r3]
        private const uint StoreGreenIfNot = 0x11C340B0;   // strhne r4, [r3]
        private const uint Always = 0xE;
        private const uint Less = 0xB;

        // Game Boy Advance: bitmap mode 3, the whole screen in Jularr purple.
        public static byte[] GameBoyAdvance()
        {
            var code = Assemble(0x080000C0, [
                Load(0, 0x04000000), Load(1, 0x0403), Word(StoreHalf),
                Load(0, 0x06000000), Load(1, 0x7CF07CF0), Load(2, 0x06012C00),
                Label("fill"), Word(StoreWordNext), Word(CompareEnd), Branch(Less, "fill"),
                Label("halt"), Branch(Always, "halt")
            ]);
            var rom = new byte[0xC0 + code.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(rom, 0xEA00002E);
            "JULARR TEST"u8.CopyTo(rom.AsSpan(0xA0));
            "AJLE00"u8.CopyTo(rom.AsSpan(0xAC));
            rom[0xB2] = 0x96;
            rom[0xBD] = (byte)-(rom.AsSpan(0xA0, 0x1D).ToArray().Sum(value => value) + 0x19);
            code.CopyTo(rom, 0xC0);
            return rom;
        }

        // Nintendo DS: both screens on, the top one purple, the bottom one green and red while it is touched.
        // The ARM7 copies its key register (bit 6 clear while the screen is touched) to main memory, where the ARM9 reads it.
        public static byte[] NintendoDs()
        {
            var arm9 = Assemble(0x02000000, [
                Load(0, 0x04000304), Load(1, 0x8203), Word(StoreHalf),
                Load(0, 0x04000000), Load(1, 0x00010000), Word(StoreWord),
                Load(0, 0x04001000), Word(StoreWord),
                Load(0, 0x05000000), Load(1, 0x7CF0), Word(StoreHalf),
                Load(0, 0x02200000), Load(3, 0x05000400), Load(4, 0x03E0), Load(5, 0x001F),
                Label("loop"), Word(LoadHalf), Word(TestTouch), Word(StoreRedIfTouched), Word(StoreGreenIfNot), Branch(Always, "loop")
            ]);
            var arm7 = Assemble(0x02380000, [
                Load(0, 0x04000136), Load(2, 0x02200000),
                Label("loop"), Word(LoadHalf), Word(StoreHalfShared), Branch(Always, "loop")
            ]);
            var arm9Offset = 0x200;
            var arm7Offset = arm9Offset + Align(arm9.Length);
            var rom = new byte[arm7Offset + Align(arm7.Length)];
            "JULARRTEST"u8.CopyTo(rom);
            "####00"u8.CopyTo(rom.AsSpan(12));
            foreach (var (offset, value) in new[] { (0x20, arm9Offset), (0x24, 0x02000000), (0x28, 0x02000000), (0x2C, arm9.Length),
                (0x30, arm7Offset), (0x34, 0x02380000), (0x38, 0x02380000), (0x3C, arm7.Length), (0x80, rom.Length), (0x84, 0x4000) })
            {
                BinaryPrimitives.WriteInt32LittleEndian(rom.AsSpan(offset), value);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x15C), 0xCF56);
            BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x15E), Crc16(rom.AsSpan(0, 0x15E)));
            arm9.CopyTo(rom, arm9Offset);
            arm7.CopyTo(rom, arm7Offset);
            return rom;
        }

        private static int Align(int length) => (length + 0x1FF) & ~0x1FF;

        private static ushort Crc16(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFF;
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
                }
            }

            return (ushort)crc;
        }

        private sealed record Line(string Kind, uint Value = 0, string? Name = null);

        private static Line Load(int register, uint value) => new("load" + register, value);
        private static Line Word(uint value) => new("word", value);
        private static Line Branch(uint condition, string label) => new("branch", condition, label);
        private static Line Label(string name) => new("label", Name: name);

        // Instructions first, then the literal pool their loads read relative to the program counter.
        private static byte[] Assemble(uint origin, Line[] lines)
        {
            var labels = new Dictionary<string, uint>();
            var address = origin;
            foreach (var line in lines)
            {
                if (line.Kind == "label") labels[line.Name!] = address;
                else address += 4;
            }

            var pool = address;
            var literals = new List<uint>();
            var words = new List<uint>();
            address = origin;
            foreach (var line in lines.Where(line => line.Kind != "label"))
            {
                if (line.Kind.StartsWith("load", StringComparison.Ordinal))
                {
                    if (!literals.Contains(line.Value)) literals.Add(line.Value);
                    var register = uint.Parse(line.Kind[4..], System.Globalization.CultureInfo.InvariantCulture);
                    words.Add(0xE59F0000 | (register << 12) | (pool + 4 * (uint)literals.IndexOf(line.Value) - (address + 8)));
                }
                else if (line.Kind == "branch")
                {
                    words.Add((line.Value << 28) | 0x0A000000 | (((labels[line.Name!] - (address + 8)) >> 2) & 0xFFFFFF));
                }
                else
                {
                    words.Add(line.Value);
                }

                address += 4;
            }

            var bytes = new byte[(words.Count + literals.Count) * 4];
            foreach (var (word, index) in words.Concat(literals).Select((word, index) => (word, index)))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), word);
            }

            return bytes;
        }
    }
}
