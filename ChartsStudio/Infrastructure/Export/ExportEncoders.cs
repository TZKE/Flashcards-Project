using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using AIFlashcardMaker.ChartsStudio.Domain.Export;

namespace AIFlashcardMaker.ChartsStudio.Infrastructure.Export;

/// <summary>Everything an encoder may consume. The service fills only what the format needs.</summary>
public sealed class EncodePayload
{
    public byte[]? PngBytes { get; init; }
    public string? SvgXml { get; init; }
    public byte[]? Rgb24 { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }

    public required ExportProfile Profile { get; init; }

    /// <summary>Logical size the SVG was rendered at (its coordinate system).</summary>
    public int LogicalWidth { get; init; }
    public int LogicalHeight { get; init; }
}

/// <summary>
/// Charts Studio Phase 5 — encoders: the last pipeline stage, bytes in → file bytes out.
///
/// Encoders are pure functions with no I/O, no ScottPlot and no WPF, so each is testable with
/// synthetic payloads down to the byte level. All three are DETERMINISTIC — no timestamps, no
/// GUIDs, no environment leakage — so exporting the same figure twice yields byte-identical
/// files. For scientific work that is a feature, not a nicety: identical inputs should be
/// provably identical outputs.
/// </summary>
public static class ExportEncoders
{
    public static byte[] Encode(ExportFormat format, EncodePayload payload) => format switch
    {
        ExportFormat.Png => EncodePng(payload),
        ExportFormat.Svg => EncodeSvg(payload),
        ExportFormat.Pdf => EncodePdf(payload),
        _ => throw new NotSupportedException($"No encoder for {format}.")
    };

    // ---------------------------------------------------------------------------------
    // PNG — the renderer already produced the encoded raster; pass it through unchanged.
    // ---------------------------------------------------------------------------------

    private static byte[] EncodePng(EncodePayload p) =>
        p.PngBytes ?? throw new InvalidOperationException("PNG payload missing.");

    // ---------------------------------------------------------------------------------
    // SVG — apply the PHYSICAL size to the vector.
    //
    // The renderer emits the figure in logical coordinates. Setting width/height in real
    // inches while preserving the viewBox rescales the whole coordinate system losslessly:
    // the exported vector prints at exactly the profile's physical size with exactly the
    // approved layout. This is the one truly resolution-independent output.
    // ---------------------------------------------------------------------------------

    private static byte[] EncodeSvg(EncodePayload p)
    {
        string svg = p.SvgXml ?? throw new InvalidOperationException("SVG payload missing.");

        // ScottPlot numbers clip-path ids from a PROCESS-GLOBAL counter, so the same figure
        // rendered twice yields "cl_5" one time and "cl_9" the next — byte-different files
        // for identical content. Renumber ids in first-seen order so identical figures export
        // to identical bytes, which is the reproducibility property the manifest promises.
        svg = CanonicalizeSvgIds(svg);

        int tagStart = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        int tagEnd = tagStart >= 0 ? svg.IndexOf('>', tagStart) : -1;
        if (tagEnd < 0) throw new InvalidOperationException("Renderer produced malformed SVG.");

        string wIn = p.Profile.WidthInches.ToString("0.###", CultureInfo.InvariantCulture);
        string hIn = p.Profile.HeightInches.ToString("0.###", CultureInfo.InvariantCulture);

        string rootTag = svg[tagStart..(tagEnd + 1)];
        rootTag = ReplaceOrAddAttribute(rootTag, "width", wIn + "in");
        rootTag = ReplaceOrAddAttribute(rootTag, "height", hIn + "in");

        // Padding in the vector world is a viewBox expansion: shifting the origin negative and
        // widening the box adds margin around the figure with zero loss — the same breathing
        // room the raster path composites, expressed in coordinates instead of pixels.
        double padLogical = p.Profile.ScaleFactor > 0
            ? p.Profile.PaddingPixels / p.Profile.ScaleFactor
            : 0;
        string pad = padLogical.ToString("0.##", CultureInfo.InvariantCulture);
        string vbW = (p.LogicalWidth + 2 * padLogical).ToString("0.##", CultureInfo.InvariantCulture);
        string vbH = (p.LogicalHeight + 2 * padLogical).ToString("0.##", CultureInfo.InvariantCulture);
        rootTag = ReplaceOrAddAttribute(rootTag, "viewBox", $"-{pad} -{pad} {vbW} {vbH}");

        string result = svg[..tagStart] + rootTag + svg[(tagEnd + 1)..];
        return Encoding.UTF8.GetBytes(result);
    }

    private static string CanonicalizeSvgIds(string svg)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        return System.Text.RegularExpressions.Regex.Replace(
            svg,
            @"cl_[0-9A-Za-z]+",
            m =>
            {
                if (!map.TryGetValue(m.Value, out string? stable))
                {
                    stable = "cl_" + map.Count;
                    map[m.Value] = stable;
                }
                return stable;
            });
    }

    private static string ReplaceOrAddAttribute(string tag, string name, string value)
    {
        var regex = new System.Text.RegularExpressions.Regex(
            $@"\b{name}\s*=\s*""[^""]*""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return regex.IsMatch(tag)
            ? regex.Replace(tag, $"{name}=\"{value}\"", 1)
            : tag.Insert(tag.Length - 1, $" {name}=\"{value}\"");
    }

    // ---------------------------------------------------------------------------------
    // PDF — a minimal, hand-written, single-page document embedding the figure as RGB24
    // at full export DPI, on a page of the exact physical size.
    //
    // WHY HAND-WRITTEN: ScottPlot has no vector PDF surface, and adding a PDF library to a
    // published, SHA-verified installer for one page type is the wrong trade. The subset of
    // PDF needed — catalog, page tree, one page, one FlateDecode image XObject, one content
    // stream — is small, fully specified, and byte-deterministic (deliberately NO
    // /CreationDate). The raster-not-vector nature is documented on the format itself.
    //
    // Structure: %PDF-1.4 header, objects 1-5, xref with exact byte offsets, trailer.
    // ---------------------------------------------------------------------------------

    private static byte[] EncodePdf(EncodePayload p)
    {
        byte[] rgb = p.Rgb24 ?? throw new InvalidOperationException("Pixel payload missing.");
        int w = p.PixelWidth, h = p.PixelHeight;
        if (rgb.Length != w * h * 3)
            throw new InvalidOperationException("Pixel payload has the wrong length.");

        string wPt = p.Profile.WidthPoints.ToString("0.##", CultureInfo.InvariantCulture);
        string hPt = p.Profile.HeightPoints.ToString("0.##", CultureInfo.InvariantCulture);

        // FlateDecode = zlib (RFC 1950), which .NET provides directly.
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(rgb, 0, rgb.Length);
            compressed = ms.ToArray();
        }

        string content = $"q {wPt} 0 0 {hPt} 0 0 cm /Im1 Do Q";
        byte[] contentBytes = Encoding.ASCII.GetBytes(content);

        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"),
            Encoding.ASCII.GetBytes("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"),
            Encoding.ASCII.GetBytes(
                $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {wPt} {hPt}] " +
                "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n"),
            Concat(
                Encoding.ASCII.GetBytes($"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n"),
                contentBytes,
                Encoding.ASCII.GetBytes("\nendstream\nendobj\n")),
            Concat(
                Encoding.ASCII.GetBytes(
                    $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} " +
                    "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
                    $"/Length {compressed.Length} >>\nstream\n"),
                compressed,
                Encoding.ASCII.GetBytes("\nendstream\nendobj\n"))
        };

        using var pdf = new MemoryStream();
        void Write(byte[] bytes) => pdf.Write(bytes, 0, bytes.Length);

        Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));

        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = pdf.Position;
            Write(objects[i]);
        }

        long xrefPos = pdf.Position;
        var xref = new StringBuilder();
        xref.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
            xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");

        Write(Encoding.ASCII.GetBytes(xref.ToString()));
        Write(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n"));

        return pdf.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(x => x.Length)];
        int pos = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, pos, part.Length);
            pos += part.Length;
        }
        return result;
    }
}
