using System.Runtime.InteropServices;
using Anthropic;
using Slopterm.Server.Vault;

namespace Slopterm.Server.Ai;

/// <summary>
/// Resolves Claude credentials for the in-terminal agent. This is the single readiness
/// authority: <see cref="ProbeSource"/> backs both <c>GET /api/ai/credential-status</c> AND the
/// pre-turn gate in the agent loop, so the Settings readout and the actual turn behavior can
/// never disagree. The key value itself is never returned or logged.
/// </summary>
public static class AnthropicCredentials
{
    /// <summary>
    /// A stored vault key wins as an explicit override; otherwise the SDK's own zero-config
    /// resolution (env vars, then an "ant auth login" OAuth profile) applies via the
    /// parameterless client.
    /// </summary>
    public static AnthropicClient BuildClient(VaultService vault)
    {
        var key = vault.GetAnthropicKey();
        return string.IsNullOrEmpty(key) ? new AnthropicClient() : new AnthropicClient { ApiKey = key };
    }

    public static (string Source, bool Ready) ProbeSource(VaultService vault)
    {
        // Reports "vault" only when the vault is unlocked AND a key is stored; otherwise falls
        // through env -> ant profile -> none, mirroring exactly what BuildClient would resolve.
        if (vault.IsUnlocked && !string.IsNullOrEmpty(vault.GetAnthropicKey()))
        {
            return ("vault", true);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            return ("env-api-key", true);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")))
        {
            return ("env-auth-token", true);
        }

        if (HasAntProfile())
        {
            return ("ant-profile", true);
        }

        return ("none", false);
    }

    private static bool HasAntProfile()
    {
        try
        {
            string dir;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData))
                {
                    return false;
                }

                dir = Path.Combine(appData, "Anthropic");
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                {
                    return false;
                }

                dir = Path.Combine(home, ".config", "anthropic");
            }

            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any();
        }
        catch
        {
            // Probing must never throw - a filesystem hiccup just means "no ant profile".
            return false;
        }
    }
}
