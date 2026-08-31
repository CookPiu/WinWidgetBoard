namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// The "start with Windows" state, expressed as the shell expresses it: a shortcut in the
/// user's Startup folder. There is no stored flag anywhere, because a second copy of this
/// fact would immediately disagree with the shell - a user who deletes the shortcut in
/// Explorer has turned the setting off, and the panel has to read that as off.
///
/// Enabling copies the Start-menu shortcut the installer already wrote rather than
/// assembling a new one. That keeps every field of the two shortcuts identical (target,
/// working directory, description) without this process linking the shell COM interfaces
/// that authoring a .lnk otherwise requires, and it means the installer stays the single
/// author of what "launching WinWidgetBoard" means.
/// </summary>
public sealed class StartupShortcut
{
    private readonly string _sourceShortcutPath;
    private readonly string _startupShortcutPath;

    public StartupShortcut(string sourceShortcutPath, string startupShortcutPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceShortcutPath);
        ArgumentException.ThrowIfNullOrEmpty(startupShortcutPath);
        _sourceShortcutPath = sourceShortcutPath;
        _startupShortcutPath = startupShortcutPath;
    }

    /// <summary>The file name both copies carry, matching what the installer writes.</summary>
    public const string ShortcutFileName = "WinWidgetBoard.lnk";

    /// <summary>
    /// The real pair of paths for this user. Both are per-user locations; nothing here needs
    /// elevation, and nothing here touches the machine-wide Run key or another user's profile.
    /// </summary>
    public static StartupShortcut ForCurrentUser() =>
        new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                ShortcutFileName),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                ShortcutFileName));

    /// <summary>
    /// False when there is nothing to copy and nothing already copied - a build run straight
    /// out of <c>artifacts\</c> has no Start-menu shortcut. The toggle then says so instead of
    /// offering a switch that cannot move.
    /// </summary>
    public bool IsAvailable => Exists(_sourceShortcutPath) || IsEnabled;

    public bool IsEnabled => Exists(_startupShortcutPath);

    /// <summary>
    /// Applies the requested state and reports whether the shell now agrees. Turning it off
    /// always works, including when the source shortcut is gone: the user must be able to
    /// stop an app from starting itself even after its installer has been removed.
    /// </summary>
    public bool TrySetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (Exists(_startupShortcutPath))
                {
                    File.Delete(_startupShortcutPath);
                }

                return !IsEnabled;
            }

            if (!Exists(_sourceShortcutPath))
            {
                return false;
            }

            string? directory = Path.GetDirectoryName(_startupShortcutPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(_sourceShortcutPath, _startupShortcutPath, overwrite: true);
            return IsEnabled;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                ArgumentException)
        {
            // A locked or redirected Startup folder is the user's environment, not a bug to
            // crash the settings dialog over. The caller reports the failure in the dialog.
            return false;
        }
    }

    private static bool Exists(string path) => File.Exists(path);
}
