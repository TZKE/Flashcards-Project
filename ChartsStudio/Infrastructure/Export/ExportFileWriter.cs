using System.IO;

namespace AIFlashcardMaker.ChartsStudio.Infrastructure.Export;

/// <summary>
/// Charts Studio Phase 5 — the only code in the export pipeline that touches the file system.
///
/// Rules it enforces:
///   • ATOMIC: bytes go to a .tmp beside the target and are moved into place, so a crash or a
///     full disk mid-write can never leave a half-figure that looks like a real one. The
///     11pm-before-a-deadline failure mode is a truncated PNG in a submission folder.
///   • HONEST ERRORS: I/O exceptions are mapped to sentences a researcher can act on, and
///     returned, not thrown — the batch decides what to do with a failure, not the writer.
/// </summary>
public static class ExportFileWriter
{
    public sealed class WriteResult
    {
        public required bool Success { get; init; }
        public string Error { get; init; } = "";
    }

    public static WriteResult Write(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return new WriteResult { Success = true };
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            return new WriteResult { Success = false, Error = Describe(ex, path) };
        }
    }

    private static string Describe(Exception ex, string path) => ex switch
    {
        UnauthorizedAccessException =>
            "Windows refused access to this folder. Choose a folder you can write to (for example, Documents).",
        DirectoryNotFoundException =>
            "That folder does not exist and could not be created. Check the destination path.",
        PathTooLongException =>
            "The full file path is too long for Windows. Choose a shorter destination or file name.",
        IOException io when io.Message.Contains("disk", StringComparison.OrdinalIgnoreCase)
                         || io.HResult == unchecked((int)0x80070070) =>
            "The disk is full. Free some space or choose another drive.",
        IOException =>
            $"The file could not be written ({ex.Message}).",
        ArgumentException or NotSupportedException =>
            "The destination path contains characters Windows cannot use.",
        _ => $"Unexpected error writing {Path.GetFileName(path)} ({ex.GetType().Name})."
    };
}
