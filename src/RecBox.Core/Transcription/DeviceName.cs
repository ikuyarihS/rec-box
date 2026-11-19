using System.Text.RegularExpressions;

namespace RecBox.Core.Transcription;

/// <summary>Turns the worker's device id ("Vulkan1 = NVIDIA GeForce RTX 3060 Laptop GPU (discrete, 5.9 GB own memory)")
/// into the plain card name people read on screen ("NVIDIA GeForce RTX 3060 Laptop GPU"). Logs and session.json keep the raw id.</summary>
public static class DeviceName
{
    private static readonly Regex Id = new(@"^\s*Vulkan\d*\s*=\s*", RegexOptions.IgnoreCase);
    private static readonly Regex Details = new(@"\s*\([^)]*\)\s*$");

    public static string? Friendly(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Details.Replace(Id.Replace(raw, ""), "").Trim();
        return s.Length == 0 || s.Equals("Vulkan", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    /// <summary>"the NVIDIA ... GPU", or "the graphics card" / "the processor" when the name is unknown.</summary>
    public static string Where(string? raw, bool gpu) =>
        gpu ? (Friendly(raw) is { } n ? Loc.F("where_named", n) : Loc.T("where_gpu")) : Loc.T("where_cpu");
}
