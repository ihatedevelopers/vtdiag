using System.Security.AccessControl;
using System.Security.Principal;

namespace VTD.Journal;

/// <summary>
/// Creates and checks the engine's private folders under <c>%ProgramData%\VTDiag\</c> (journal, backups, quarantine,
/// state). Non-admins may create folders in <c>%ProgramData%</c>, so a folder that already exists is trusted only when it
/// is a real directory (no junction or symlink) owned by SYSTEM, Administrators or TrustedInstaller (critique S-a); its
/// DACL is then reset to SYSTEM + Administrators full control, inherited, with no other access.
/// </summary>
public static class SecureDirectory
{
    /// <summary>SID of SYSTEM.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>SID of BUILTIN\Administrators.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>SID of NT SERVICE\TrustedInstaller.</summary>
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Owners trusted for elevated file operations.</summary>
    public static IReadOnlyList<string> TrustedOwners { get; } = [SystemSid, AdministratorsSid, TrustedInstallerSid];

    /// <summary>
    /// Ensures <paramref name="root"/> and every folder from it down to <paramref name="path"/> with <see cref="Ensure"/>, so no
    /// level between the trusted root and the target can be a player-owned folder or a junction.
    /// </summary>
    /// <param name="root">The VTDiag data root (for example <c>%ProgramData%\VTDiag</c>).</param>
    /// <param name="path">A folder at or under <paramref name="root"/>.</param>
    /// <param name="adminOnly">Apply and require the admin-only ACL.</param>
    public static void EnsureUnder(string root, string path, bool adminOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The folder is not under the VTDiag data root.", nameof(path));
        }

        Ensure(fullRoot, adminOnly);
        var current = fullRoot;
        foreach (var part in fullPath[fullRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            Ensure(current, adminOnly);
        }
    }

    /// <summary>
    /// Ensures <paramref name="path"/> exists as a real directory. With <paramref name="adminOnly"/> it must be (or is created)
    /// owned by a trusted owner and gets the protected admin-only DACL. Only the folder itself is checked: use
    /// <see cref="EnsureUnder"/> for a nested folder.
    /// </summary>
    /// <param name="path">Absolute directory path whose parent exists.</param>
    /// <param name="adminOnly">Apply and require the admin-only ACL (production). Tests in temp folders pass false.</param>
    /// <exception cref="UnauthorizedAccessException">The folder exists but is a reparse point or has an untrusted owner.</exception>
    public static void Ensure(string path, bool adminOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A secure directory path must be absolute.", nameof(path));
        }

        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Refusing a VTDiag folder that is a junction or symbolic link: " + path);
            }

            if (adminOnly)
            {
                var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value;
                if (owner is null || !TrustedOwners.Contains(owner, StringComparer.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Refusing a VTDiag folder not owned by SYSTEM or Administrators: " + path);
                }

                info.SetAccessControl(AdminOnlySecurity());
            }

            return;
        }

        if (info.Parent is { Exists: false } parent)
        {
            throw new DirectoryNotFoundException("The parent folder does not exist (use EnsureUnder): " + parent.FullName);
        }

        if (adminOnly)
        {
            info.Create(AdminOnlySecurity());
        }
        else
        {
            info.Create();
        }

        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("A VTDiag folder turned into a reparse point while it was created: " + path);
        }
    }

    /// <summary>The protected DACL: SYSTEM and Administrators full control (inherited by children), owner Administrators.</summary>
    public static DirectorySecurity AdminOnlySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(SystemSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AdministratorsSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(new SecurityIdentifier(AdministratorsSid));
        return security;
    }
}
