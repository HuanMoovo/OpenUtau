using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenUtau.Core.Util {
    /// <summary>
    /// Version strings of this fork, and the rules the update check compares them with.
    ///
    /// Our releases are tagged <c>[v]&lt;core&gt;-fork.&lt;n&gt;</c>, e.g. <c>v0.1.565-fork.2</c>:
    /// the numeric core is the upstream version this fork tracks, the counter is ours.
    /// <see cref="Version"/> cannot read that string, and a plain numeric compare would
    /// offer upstream's release of the same core over our own build, so the version math
    /// lives here - outside the updater UI - where it can be tested on its own.
    /// </summary>
    public static class VersionComparison {
        /// <summary>The repository releases are read from. Upstream's releases are not ours.</summary>
        public const string RepoSlug = "HuanMoovo/OpenUtau";
        public const string RepoUrl = "https://github.com/" + RepoSlug;
        /// <summary>
        /// Release list of <see cref="RepoSlug"/>. per_page=100 because a run of
        /// releases could otherwise push the newest one off the first page.
        /// </summary>
        public const string ReleasesApiUrl = "https://api.github.com/repos/" + RepoSlug + "/releases?per_page=100";

        static readonly Regex forkSuffix = new(
            @"^fork\.(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A version string taken apart into its numeric core and fork counter.</summary>
        public readonly struct Parsed {
            public Parsed(bool success, Version core, bool isFork, int forkCounter) {
                Success = success;
                Core = core;
                IsFork = isFork;
                ForkCounter = forkCounter;
            }

            /// <summary>False when the text was not a version at all.</summary>
            public bool Success { get; }
            /// <summary>Numeric core, always four components: 0.1.565.0 for "0.1.565-fork.2".</summary>
            public Version Core { get; }
            /// <summary>True when the text carried a "-fork.N" suffix.</summary>
            public bool IsFork { get; }
            /// <summary>Our release counter; 0 without a "-fork.N" suffix.</summary>
            public int ForkCounter { get; }
            /// <summary>
            /// True when there is no usable version to compare: an unparseable string, or the
            /// all-zero version of a build whose version was never stamped (the 0.0.0 placeholder).
            /// </summary>
            public bool IsUnset => !Success || (Core.Major == 0 && Core.Minor == 0 && Core.Build == 0);

            public override string ToString() => !Success ? "(unparseable)"
                : IsFork ? $"{Core}-fork.{ForkCounter}"
                : Core.ToString();
        }

        /// <summary>Reads "[v]X.Y.Z[.W][-fork.N][+metadata]". Never throws.</summary>
        public static Parsed Parse(string? text) {
            string value = (text ?? string.Empty).Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) {
                value = value.Substring(1);
            }
            // Build metadata (the "+&lt;sha&gt;" the SDK can append) is not compared.
            int metadata = value.IndexOf('+');
            if (metadata >= 0) {
                value = value.Substring(0, metadata);
            }
            string core = value;
            string suffix = string.Empty;
            int dash = value.IndexOf('-');
            if (dash >= 0) {
                core = value.Substring(0, dash);
                suffix = value.Substring(dash + 1);
            }
            if (!Version.TryParse(core.Trim(), out Version? parsed)) {
                return default;
            }
            Match match = forkSuffix.Match(suffix.Trim());
            int counter = 0;
            bool isFork = match.Success && int.TryParse(
                match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out counter);
            return new Parsed(
                success: true,
                core: new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0)),
                isFork: isFork,
                forkCounter: isFork ? counter : 0);
        }

        /// <summary><see cref="Parse"/> for callers that only care whether it worked.</summary>
        public static bool TryParse(string? text, out Parsed version) {
            version = Parse(text);
            return version.Success;
        }

        /// <summary>True when a tag belongs to a fork release ("v0.1.565-fork.2").</summary>
        public static bool IsForkRelease(string? tag) => Parse(tag).IsFork;

        /// <summary>The tag without its leading "v"/"V", as shown to the user.</summary>
        public static string StripTagPrefix(string? tag) {
            string value = (tag ?? string.Empty).Trim();
            return value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value.Substring(1) : value;
        }

        /// <summary>
        /// Whether a release tagged <paramref name="candidateTag"/> is an update for a build
        /// reporting <paramref name="buildVersion"/>.
        ///
        /// All four rules are needed by a fork:
        /// <list type="bullet">
        /// <item>only our own fork releases are offers - an upstream tag (no fork counter) never
        /// updates a fork build, whatever its core says;</item>
        /// <item>a higher core wins outright, so a fork release rebased onto a newer upstream
        /// version is offered to older fork builds;</item>
        /// <item>on the same core the fork counter decides: -fork.2 is newer than -fork.1, and
        /// two equal versions are never an update;</item>
        /// <item>a build whose own version cannot be read (the 0.0.0 placeholder) has nothing to
        /// compare against, so it is never told there is an update.</item>
        /// </list>
        /// </summary>
        public static bool IsNewerRelease(string? candidateTag, string? buildVersion) {
            Parsed candidate = Parse(candidateTag);
            Parsed build = Parse(buildVersion);
            if (candidate.IsUnset || build.IsUnset || !candidate.IsFork) {
                return false;
            }
            int core = candidate.Core.CompareTo(build.Core);
            return core != 0 ? core > 0 : candidate.ForkCounter > build.ForkCounter;
        }
    }
}
