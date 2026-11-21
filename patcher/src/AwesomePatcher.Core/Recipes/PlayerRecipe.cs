using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AwesomePatcher.Core
{
    // Only the fingerprinted Unity 4.5.5p5 x86 player has these argument readers.
    // All five relative call sites displace no base relocations.
    internal static class PlayerRecipe
    {
        internal const string Player = "Awesome.exe";
        internal const string Helper = "Awesome_DirectToRift.exe";
        internal const string Startup = "AwesomeStartup.bin";
        internal const string PlayerOriginal = "E913CB114A94EC190E45266AD44721A8EC3636D364CEA9CB0AECEC7717006CE2";
        internal const string HelperOriginal = "47AD5AD4962138DF0A9AD26315E297AEA36343F150E292B805106A99E9BAB396";

        internal static byte[] Rewrite(byte[] original, byte[] startup)
        {
            if (PatchFiles.Digest(original) != PlayerOriginal)
                throw new InvalidDataException("Unsupported Awesome.exe.");
            const int header = 0x340, optional = 0x148, sectionRva = 0xba7000, prefix = 24;
            if (startup.Length <= prefix || startup.Length > 65536 || Encoding.ASCII.GetString(startup, 0, 4) != "AFX4")
                throw new InvalidDataException("Unsupported Awesome startup payload.");
            int size = startup.Length - prefix;
            int aligned = (size + 511) & ~511;
            var result = new byte[original.Length + aligned];
            original.CopyTo(result, 0);
            if (BitConverter.ToUInt16(result, 0x136) != 7 ||
                BitConverter.ToInt32(result, optional + 56) != sectionRva ||
                result.Skip(header).Take(40).Any(b => b != 0))
                throw new InvalidDataException("Unexpected Unity player section layout.");

            using (var writer = new BinaryWriter(new MemoryStream(result, true)))
            {
                writer.BaseStream.Position = 0x136;
                writer.Write((ushort)8);
                writer.BaseStream.Position = optional + 4;
                writer.Write(BitConverter.ToInt32(original, optional + 4) + aligned);
                writer.BaseStream.Position = optional + 56;
                writer.Write(sectionRva + ((size + 4095) & ~4095));
                writer.BaseStream.Position = optional + 64;
                writer.Write(0); // Optional image checksum for this user-mode executable.
                writer.BaseStream.Position = header;
                writer.Write(Encoding.ASCII.GetBytes(".aftxr\0\0"));
                writer.Write(size);
                writer.Write(sectionRva);
                writer.Write(aligned);
                writer.Write(original.Length);
                writer.BaseStream.Position = header + 36;
                writer.Write(0x60000020); // read + execute, code
                writer.BaseStream.Position = original.Length;
                writer.Write(startup, prefix, size);

                WriteCall(writer, original, 0x1f2a85, 0x1f3685, new byte[] { 0x89, 0x45, 0xbc, 0x3b, 0xc3 },
                    sectionRva, BitConverter.ToInt32(startup, 4), size);
                WriteCall(writer, original, 0x2180d, 0x2240d, new byte[] { 0x89, 0x45, 0xf4, 0x8b, 0x45, 0xfc },
                    sectionRva, BitConverter.ToInt32(startup, 8), size);
                WriteCall(writer, original, 0x1f2ae8, 0x1f36e8, new byte[] { 0xe8, 0x83, 0xa2, 0x00, 0x00 },
                    sectionRva, BitConverter.ToInt32(startup, 12), size);
                WriteCall(writer, original, 0x411fc0, 0x412bc0, new byte[] { 0xe8, 0x7b, 0x49, 0xff, 0xff },
                    sectionRva, BitConverter.ToInt32(startup, 16), size);
                WriteCall(writer, original, 0x40577c, 0x40637c, new byte[] { 0x89, 0x75, 0xfc, 0xff, 0xd0 },
                    sectionRva, BitConverter.ToInt32(startup, 20), size);
            }
            return result;
        }

        private static void WriteCall(BinaryWriter writer, byte[] original, int offset, int rva,
            byte[] expected, int section, int entry, int size)
        {
            if (entry < 0 || entry >= size || !original.Skip(offset).Take(expected.Length).SequenceEqual(expected))
                throw new InvalidDataException("Unexpected startup entry or Unity call site.");
            writer.BaseStream.Position = offset;
            writer.Write((byte)0xe8);
            writer.Write(section + entry - (rva + 5));
            if (expected.Length == 6) writer.Write((byte)0x90);
        }
    }
}
