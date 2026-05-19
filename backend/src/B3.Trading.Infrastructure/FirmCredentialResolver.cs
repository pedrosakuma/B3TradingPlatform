using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Infrastructure;

/// <summary>
/// #126. Materialises the effective access-key bytes for a single firm,
/// honouring the legacy plain <see cref="FirmConfig.AccessKey"/> shape AND
/// the new <see cref="FirmCredentialsConfig"/> with file-mounted secret
/// indirection.
///
/// <para>
/// File mode is enforced on Linux: the secret file must be owned by the
/// current user and have permissions <c>0600</c> or <c>0400</c>. World-
/// or group-readable files are rejected so a misconfigured volume mount
/// fails fast instead of leaking credentials. On non-Linux platforms
/// (Windows test runners, mac dev loops) the permission check is skipped
/// — those environments rely on filesystem ACLs the SDK can't introspect
/// portably; we log a one-line note instead.
/// </para>
///
/// <para>
/// Pass-1 design (#126): callers receive a single <see cref="string"/>;
/// the SDK already copies the bytes into <c>Credentials.FromUtf8</c>, so
/// we don't widen the type to <c>byte[]</c> — that would force a
/// double-copy with no security benefit (the SDK's internal buffer is
/// the longest-lived holder of the secret in the process).
/// </para>
/// </summary>
public static class FirmCredentialResolver
{
    /// <summary>
    /// Returns the materialised access key for <paramref name="firm"/>.
    /// Throws <see cref="InvalidOperationException"/> if no valid secret
    /// source is configured or the file fails the permission check; this
    /// surface bubbles out of host startup so an operator sees a clean
    /// error before the FIXP session attempts to Negotiate.
    /// </summary>
    /// <param name="firm">The firm config bound from <c>Trading:Exchange:Firms[i]</c>.</param>
    /// <param name="logger">Optional logger for the legacy-shape deprecation WARN and file-mode notes.</param>
    public static string ResolveAccessKey(FirmConfig firm, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(firm);

        if (firm.Credentials is not null)
        {
            if (!string.IsNullOrEmpty(firm.AccessKey))
            {
                logger?.LogWarning(
                    "Firm {Firm} has both legacy AccessKey and structured Credentials configured; the structured Credentials wins. Remove the legacy AccessKey to silence this warning.",
                    firm.FirmId);
            }
            return ResolveFromStructured(firm.FirmId, firm.Credentials, logger);
        }

        if (string.IsNullOrEmpty(firm.AccessKey))
            throw new InvalidOperationException(
                $"Firm '{firm.FirmId}' has no credentials configured. Set Trading:Exchange:Firms[i]:Credentials or the legacy Trading:Exchange:Firms[i]:AccessKey.");

        logger?.LogWarning(
            "Firm {Firm} uses the legacy flat AccessKey shape. Migrate to Credentials: {{ Mode: AccessKey, AccessKeyFile: \"/run/secrets/...\" }} to load the secret from a mounted file. This shape will be removed in a future release.",
            firm.FirmId);
        return firm.AccessKey;
    }

    private static string ResolveFromStructured(string firmId, FirmCredentialsConfig creds, ILogger? logger)
    {
        switch (creds.Mode)
        {
            case FirmCredentialsMode.AccessKey:
                var hasInline = !string.IsNullOrEmpty(creds.AccessKey);
                var hasFile = !string.IsNullOrWhiteSpace(creds.AccessKeyFile);
                if (hasInline && hasFile)
                    throw new InvalidOperationException(
                        $"Firm '{firmId}' Credentials sets both AccessKey and AccessKeyFile; exactly one is required.");
                if (!hasInline && !hasFile)
                    throw new InvalidOperationException(
                        $"Firm '{firmId}' Credentials.Mode=AccessKey requires either AccessKey or AccessKeyFile to be set.");
                if (hasInline)
                    return creds.AccessKey!;
                return ReadSecretFile(firmId, creds.AccessKeyFile!, logger);

            default:
                throw new InvalidOperationException(
                    $"Firm '{firmId}' Credentials.Mode={creds.Mode} is not supported by the wired B3.EntryPoint.Client SDK.");
        }
    }

    private static string ReadSecretFile(string firmId, string path, ILogger? logger)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Firm '{firmId}' AccessKeyFile '{path}' does not exist.");

        EnforceFileMode(firmId, path, logger);

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Firm '{firmId}' AccessKeyFile '{path}' could not be read: {ex.Message}", ex);
        }

        // Mounted-secret files frequently carry a trailing newline (k8s
        // Secret, docker secrets, `echo` redirect). Trim once at load
        // time so the gateway never spends a Negotiate round-trip on a
        // \n-padded credential.
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException(
                $"Firm '{firmId}' AccessKeyFile '{path}' is empty after trimming whitespace.");
        return trimmed;
    }

    private static void EnforceFileMode(string firmId, string path, ILogger? logger)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            logger?.LogInformation(
                "Firm {Firm} AccessKeyFile permission check skipped on non-Linux platform; rely on filesystem ACLs.",
                firmId);
            return;
        }

        UnixFileMode perms;
        try
        {
            perms = File.GetUnixFileMode(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Firm '{firmId}' AccessKeyFile '{path}' stat failed: {ex.Message}", ex);
        }

        // Allow only 0600 (rw-------) or 0400 (r--------). Anything
        // group- or world-readable is a misconfigured mount and we
        // refuse to load the secret.
        const UnixFileMode OwnerRw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode OwnerR = UnixFileMode.UserRead;
        if (perms != OwnerRw && perms != OwnerR)
        {
            throw new InvalidOperationException(
                $"Firm '{firmId}' AccessKeyFile '{path}' has insecure permissions {ToOctal(perms)}; must be 600 or 400 (owner-only). Run: chmod 600 {path}");
        }
    }

    private static string ToOctal(UnixFileMode mode)
    {
        var bits = (int)mode & 0x1FF; // low 9 perm bits
        return Convert.ToString(bits, 8).PadLeft(3, '0');
    }
}
