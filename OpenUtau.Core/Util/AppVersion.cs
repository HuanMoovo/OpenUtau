using System;
using System.Reflection;

namespace OpenUtau.Core.Util {
    /// <summary>
    /// The version the running app reports.
    ///
    /// It comes from the assembly's informational version, which OpenUtau.csproj composes
    /// from two halves: &lt;Version&gt; (the upstream core this fork tracks) and
    /// &lt;ForkRelease&gt; (our release counter) - "0.1.565-fork.2". The assembly version is
    /// not enough on its own: the SDK derives it from the numeric core only (0.1.565.0),
    /// so the fork counter would be lost.
    /// </summary>
    public static class AppVersion {
        /// <summary>Reported when nothing at all can be read from the assembly.</summary>
        public const string Unknown = "0.0.0.0";

        /// <summary>Version of the running app, e.g. "0.1.565-fork.2".</summary>
        public static string Display { get; } = Of(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());

        /// <summary>Numeric core of <see cref="Display"/>; 0.0.0.0 when it cannot be read.</summary>
        public static Version Core { get; } = VersionComparison.Parse(Display).Core;

        /// <summary>True when this build is one of our fork releases.</summary>
        public static bool IsFork => VersionComparison.IsForkRelease(Display);

        /// <summary>
        /// <see cref="Display"/> with the release channel of the build appended, e.g.
        /// "1.5.1.3 (alpha)". This is what the window title and the updater show.
        /// </summary>
        public static string WithChannel {
            get {
                string suffix = ReleaseChannel.FromVersion(Core) is { } channel ? $" ({channel})" : string.Empty;
                return $"{Display}{suffix}";
            }
        }

        /// <summary>
        /// The version any assembly reports: its informational version with the build metadata
        /// ("+&lt;sha&gt;") trimmed, falling back to its assembly version. Public so tests can
        /// read the version a built OpenUtau.dll carries, wherever the test host runs.
        /// </summary>
        public static string Of(Assembly? assembly) {
            string? informational = assembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (informational != null && VersionComparison.TryParse(informational, out _)) {
                int metadata = informational.IndexOf('+');
                return metadata >= 0 ? informational.Substring(0, metadata) : informational;
            }
            return assembly?.GetName().Version?.ToString() ?? Unknown;
        }
    }
}
