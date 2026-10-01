using System.Reflection;

namespace BereanResourceApi;

/// <summary>
/// The version this build was stamped with (MinVer, from git tags) and the commit it was built from.
/// Served at /version so the HomeOps deploy module can confirm which build is actually running.
/// </summary>
public static class BuildInfo
{
    public static object Current { get; } = Read();

    private static object Read()
    {
        // MinVer + SourceLink produce "<version>+<commit>"
        var info = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = info.IndexOf('+');
        return new
        {
            version = plus < 0 ? info : info[..plus],
            commit = plus < 0 ? null : info[(plus + 1)..],
        };
    }
}
