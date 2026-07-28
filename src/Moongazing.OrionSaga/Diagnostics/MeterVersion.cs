namespace Moongazing.OrionSaga.Diagnostics;

using System.Reflection;

/// <summary>
/// Resolves the version stamped on the OrionSaga <see cref="System.Diagnostics.Metrics.Meter"/>
/// from the assembly's informational version, so the telemetry version tracks the package version
/// automatically instead of drifting behind a hardcoded literal. The build flows
/// <c>&lt;Version&gt;</c> into <see cref="AssemblyInformationalVersionAttribute"/>; any build
/// metadata after a <c>+</c> is trimmed off.
/// </summary>
internal static class MeterVersion
{
    /// <summary>The resolved meter version, computed once for the lifetime of the process.</summary>
    public static string Value { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(MeterVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
