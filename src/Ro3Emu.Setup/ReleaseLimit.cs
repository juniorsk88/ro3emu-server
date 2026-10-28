namespace Ro3Emu.Setup;

/// <summary>
/// The newest publisher release this RO3Emu is known to work with. A distributor (for example a server's launcher) puts
/// that number in release-limit.txt next to RO3Emu.exe, or in RO3EMU_MAX_RELEASE; <c>update</c> then keeps the game on
/// its current release instead of replacing it with one the patch has not been checked against. Without the file or
/// the variable there is no limit.
/// </summary>
internal static class ReleaseLimit
{
    public const string FileName = "release-limit.txt";
    public const string EnvironmentVariable = "RO3EMU_MAX_RELEASE";

    public static long? Read(string appDir)
    {
        if (Parse(Environment.GetEnvironmentVariable(EnvironmentVariable)) is { } fromEnvironment) return fromEnvironment;
        try
        {
            var path = Path.Combine(appDir, FileName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static long? Parse(string? text) => long.TryParse(text?.Trim(), out var value) && value > 0 ? value : null;

    /// <summary>An update is held back when the offered release is above the limit; a first install has nothing to protect.</summary>
    public static bool HoldsBack(long offered, long? limit, bool gameAlreadyInstalled) => gameAlreadyInstalled && limit is { } max && offered > max;
}
