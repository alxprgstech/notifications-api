using System.Buffers.Binary;
using System.Text;

namespace Notify;

public static class IconValidator
{
    public static string Validate(byte[] b)
    {
        if (b.Length is < 30 or > 262144) throw new Fault(400, "icon_size_invalid");
        int w, h; string type;
        if (b.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && Encoding.ASCII.GetString(b, 12, 4) == "IHDR")
        { w = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16, 4)); h = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20, 4)); type = "image/png"; }
        else if (Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP" && BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4, 4)) == b.Length - 8)
        {
            var format = Encoding.ASCII.GetString(b, 12, 4);
            if (format == "VP8X") { if ((b[20] & 2) != 0) throw new Fault(400, "animated_icon_not_supported"); w = 1 + b[24] + (b[25] << 8) + (b[26] << 16); h = 1 + b[27] + (b[28] << 8) + (b[29] << 16); }
            else if (format == "VP8L" && b[20] == 0x2f) { w = 1 + b[21] + ((b[22] & 0x3f) << 8); h = 1 + (b[22] >> 6) + (b[23] << 2) + ((b[24] & 0xf) << 10); }
            else if (format == "VP8 " && b[23] == 0x9d && b[24] == 1 && b[25] == 0x2a) { w = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26, 2)) & 0x3fff; h = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28, 2)) & 0x3fff; }
            else throw new Fault(400, "invalid_webp"); type = "image/webp";
        }
        else throw new Fault(400, "icon_format_invalid");
        if (w is < 1 or > 512 || h is < 1 or > 512) throw new Fault(400, "icon_dimensions_invalid"); return type;
    }
}
