using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.Test {
    /// <summary>
    /// The version rules behind the update check. A fork breaks every assumption the
    /// updater was written with: our versions carry a "-fork.N" counter System.Version
    /// cannot parse, our releases are not upstream's, and a build whose version was never
    /// stamped must not tell the user anything.
    /// </summary>
    public class VersionComparisonTest {
        static UpdaterViewModel.GithubRelease Release(
            string tag, bool prerelease = false, bool draft = false, long id = 1) => new() {
                tag_name = tag,
                prerelease = prerelease,
                draft = draft,
                id = id,
                html_url = $"https://github.com/HuanMoovo/OpenUtau/releases/tag/{tag}",
            };

        // ---- the repository the check asks ----

        [Fact]
        public void CheckerAsksOurForkOnly() {
            Assert.Equal("HuanMoovo/OpenUtau", VersionComparison.RepoSlug);
            Assert.Equal("https://github.com/HuanMoovo/OpenUtau", VersionComparison.RepoUrl);
            Assert.Equal(
                "https://api.github.com/repos/HuanMoovo/OpenUtau/releases?per_page=100",
                VersionComparison.ReleasesApiUrl);
            // Upstream's owner must not appear anywhere in what we query.
            Assert.DoesNotContain("stakira", VersionComparison.ReleasesApiUrl, StringComparison.OrdinalIgnoreCase);
        }

        // ---- a build of this repository reports a stamped fork version ----

        [Fact]
        public void BuiltAppCarriesAStampedForkVersion() {
            // Read straight off the OpenUtau assembly that the tests run against, so this
            // checks the version a build of this tree actually reports. The number itself is
            // patched by CI at release time, hence the shape assertions.
            string version = AppVersion.Of(typeof(UpdaterViewModel).Assembly);
            Assert.True(VersionComparison.TryParse(version, out var parsed), $"unparseable app version '{version}'");
            Assert.False(parsed.IsUnset, $"the app must not build with an unset version ('{version}')");
            Assert.True(parsed.IsFork, $"the app version must carry the fork counter ('{version}')");
        }

        [Fact]
        public void AppVersionCoreMatchesTheNumericHalf() {
            Assert.Equal(new Version("0.1.565.0"), VersionComparison.Parse("0.1.565-fork.2").Core);
        }

        // ---- parsing ----

        [Theory]
        [InlineData("0.1.565-fork.2", "0.1.565.0", 2, true)]
        [InlineData("v0.1.565-fork.2", "0.1.565.0", 2, true)]
        [InlineData("V0.1.565-fork.10", "0.1.565.0", 10, true)]
        // The SDK can append build metadata; it is not part of the comparison.
        [InlineData("0.1.565-fork.2+9d4b4a3", "0.1.565.0", 2, true)]
        // Upstream's own version strings, which the fork must read without choking.
        [InlineData("0.1.565", "0.1.565.0", 0, false)]
        [InlineData("0.1.565-beta", "0.1.565.0", 0, false)]
        [InlineData("0.1.574.4-alpha", "0.1.574.4", 0, false)]
        // Missing components read as zero rather than -1, so 1.5 and 1.5.0 compare equal.
        [InlineData("1.5", "1.5.0.0", 0, false)]
        public void ParseReadsForkAndUpstreamVersions(string text, string core, int forkCounter, bool isFork) {
            Assert.True(VersionComparison.TryParse(text, out var parsed));
            Assert.Equal(new Version(core), parsed.Core);
            Assert.Equal(forkCounter, parsed.ForkCounter);
            Assert.Equal(isFork, parsed.IsFork);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("v")]
        [InlineData("not a version")]
        [InlineData("fork.2")]
        public void ParseRejectsGarbageInsteadOfThrowing(string? text) {
            Assert.False(VersionComparison.TryParse(text, out _));
            Assert.True(VersionComparison.Parse(text).IsUnset);
        }

        [Theory]
        [InlineData("0.0.0", true)]
        [InlineData("0.0.0.0", true)]
        [InlineData("0.0.1", false)]
        [InlineData("0.1.0", false)]
        public void PlaceholderVersionsCountAsUnset(string text, bool isUnset) {
            Assert.Equal(isUnset, VersionComparison.Parse(text).IsUnset);
        }

        [Theory]
        [InlineData("v0.1.565-fork.2", "0.1.565-fork.2")]
        [InlineData("0.1.565-fork.2", "0.1.565-fork.2")]
        [InlineData("V1.2.3", "1.2.3")]
        [InlineData("", "")]
        public void StripTagPrefix(string tag, string expected) {
            Assert.Equal(expected, VersionComparison.StripTagPrefix(tag));
        }

        // ---- the comparison itself ----

        [Theory]
        // Upstream releases exist only upstream: never ours, however new the core is.
        [InlineData("0.1.565", "0.1.565-fork.2", false)]
        [InlineData("0.1.574.4-alpha", "0.1.565-fork.2", false)]
        [InlineData("1.5.0-beta", "0.1.565-fork.2", false)]
        // The next fork release is ours and is newer.
        [InlineData("v0.1.565-fork.3", "0.1.565-fork.2", true)]
        // A fork release rebased onto a newer upstream core beats a higher counter of the old core.
        [InlineData("v0.1.566-fork.1", "0.1.565-fork.2", true)]
        [InlineData("v0.1.564-fork.9", "0.1.565-fork.2", false)]
        // Already built it: the older release and the same version are never updates.
        [InlineData("v0.1.565-fork.1", "0.1.565-fork.2", false)]
        [InlineData("v0.1.565-fork.2", "0.1.565-fork.2", false)]
        // A build whose version cannot be read never nags the user.
        [InlineData("v0.1.565-fork.3", "0.0.0.0", false)]
        [InlineData("v0.1.565-fork.3", "0.0.0", false)]
        [InlineData("v0.1.565-fork.3", null, false)]
        [InlineData("v0.1.565-fork.3", "", false)]
        [InlineData("v0.1.565-fork.3", "not a version", false)]
        // Neither is a candidate that cannot be read.
        [InlineData("", "0.1.565-fork.2", false)]
        [InlineData("not a version", "0.1.565-fork.2", false)]
        public void IsNewerRelease(string candidate, string? build, bool expected) {
            Assert.Equal(expected, VersionComparison.IsNewerRelease(candidate, build));
        }

        // ---- what the check would offer, end to end minus the network ----

        [Fact]
        public void UpstreamReleasesAreNeverOffered() {
            var releases = new List<UpdaterViewModel.GithubRelease> {
                Release("0.1.565", id: 1),                                          // upstream stable
                Release("0.1.574.4-alpha", prerelease: true, id: 2),                // upstream alpha
                Release("0.1.565-beta", prerelease: true, id: 3),
            };
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.1.565-fork.2"));
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "beta", "0.1.565-fork.2"));
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "alpha", "0.1.565-fork.2"));
        }

        [Fact]
        public void NextForkReleaseIsOffered() {
            var releases = new List<UpdaterViewModel.GithubRelease> {
                Release("v0.1.565-fork.1", id: 1),
                Release("v0.1.565-fork.2", id: 2),
                Release("v0.1.565-fork.3", id: 3),
                Release("0.1.574.4-alpha", prerelease: true, id: 4),  // upstream's newest, ignored
            };
            var newest = UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.1.565-fork.2");
            Assert.NotNull(newest);
            Assert.Equal("v0.1.565-fork.3", newest.tag_name);
        }

        [Fact]
        public void EqualOrOlderReleasesAreNotOffered() {
            var releases = new List<UpdaterViewModel.GithubRelease> {
                Release("v0.1.565-fork.1", id: 1),
                Release("v0.1.565-fork.2", id: 2),
            };
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.1.565-fork.2"));
        }

        [Fact]
        public void UnparseableBuildVersionIsNeverOffered() {
            var releases = new List<UpdaterViewModel.GithubRelease> {
                Release("v0.1.565-fork.3", id: 1),
                Release("v0.1.566-fork.1", id: 2),
            };
            // The 0.0.0 placeholder of a build nobody stamped, and every other unreadable form.
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.0.0.0"));
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.0.0"));
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "stable", null));
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "stable", ""));
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "stable", "banana"));
        }

        [Fact]
        public void HighestVersionWinsNotTheLatestPublication() {
            var releases = new List<UpdaterViewModel.GithubRelease> {
                Release("v0.1.566-fork.1", id: 1),   // higher core, published first
                Release("v0.1.565-fork.9", id: 9),   // published later, still the older core
            };
            var newest = UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.1.565-fork.2");
            Assert.NotNull(newest);
            Assert.Equal("v0.1.566-fork.1", newest.tag_name);
        }

        [Fact]
        public void DraftsAreNeverOffered() {
            var releases = new List<UpdaterViewModel.GithubRelease> {
                Release("v0.1.565-fork.3", draft: true, id: 3),
            };
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.1.565-fork.2"));
        }

        [Fact]
        public void ForkReleaseIsOfferedOnEveryChannel() {
            // Our fork publishes one stream, so the upstream channel split must not hide it.
            var releases = new List<UpdaterViewModel.GithubRelease> {
                Release("v0.1.565-fork.3", id: 3),
            };
            Assert.NotNull(UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.1.565-fork.2"));
            Assert.NotNull(UpdaterViewModel.SelectNewestRelease(releases, "beta", "0.1.565-fork.2"));
            Assert.NotNull(UpdaterViewModel.SelectNewestRelease(releases, "alpha", "0.1.565-fork.2"));
        }

        [Fact]
        public void PlanShowsOurReleaseAndLinksToIt() {
            var plan = UpdaterViewModel.PlanRelease(Release("v0.1.565-fork.3"));
            Assert.NotNull(plan);
            Assert.Equal("0.1.565-fork.3", plan.version);
            Assert.Equal("https://github.com/HuanMoovo/OpenUtau/releases/tag/v0.1.565-fork.3", plan.releaseUrl);
            // Our releases ship a plain zip: there is no appcast for Sparkle to install,
            // so the dialog sends the user to the release page instead.
            Assert.Null(plan.appcastUrl);
        }

        [Fact]
        public void PlanUsesTheAppcastWhenTheReleaseShipsOne() {
            var release = Release("v0.1.565-fork.3");
            string appcastUrl = "https://github.com/HuanMoovo/OpenUtau/releases/download/v0.1.565-fork.3/appcast.xml";
            release.assets = new[] {
                new UpdaterViewModel.GithubReleaseAsset {
                    name = $"appcast.{OS.GetUpdaterRid()}.xml",
                    browser_download_url = appcastUrl,
                },
            };
            var plan = UpdaterViewModel.PlanRelease(release);
            Assert.NotNull(plan);
            Assert.Equal("0.1.565-fork.3", plan.version);
            Assert.Equal(appcastUrl, plan.appcastUrl);
        }

        [Fact]
        public void PlanIsNullWhenThereIsNowhereToSendTheUser() {
            var release = Release("v0.1.565-fork.3");
            release.html_url = string.Empty;
            Assert.Null(UpdaterViewModel.PlanRelease(release));
        }

        // ---- what the check does with real release payloads ----

        [Fact]
        public void OurRepoPayloadOffersTheNextForkRelease() {
            var releases = ReadReleases("ours.json");
            // A build of fork.1 finds fork.2, on our own release page ...
            var newest = UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.1.565-fork.1");
            Assert.NotNull(newest);
            var plan = UpdaterViewModel.PlanRelease(newest);
            Assert.NotNull(plan);
            Assert.Equal("0.1.565-fork.2", plan.version);
            Assert.Equal("https://github.com/HuanMoovo/OpenUtau/releases/tag/v0.1.565-fork.2", plan.releaseUrl);
            // ... and the newest fork build is offered nothing.
            Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, "stable", "0.1.565-fork.2"));
        }

        [Fact]
        public void UpstreamTagNamesAreNeverOffered() {
            // Upstream's releases as of the fork's 0.1.565, by their real tag names: the stable
            // one is what the check used to see and offer, the alphas and the beta are what it
            // would see on the other channels. None of them - on any channel - may reach a fork
            // build, whether it reports a fork version or an unreadable one.
            var releases = new List<UpdaterViewModel.GithubRelease> {
                Release("0.1.574.4-alpha", prerelease: true, id: 6),
                Release("0.1.574.3-alpha", prerelease: true, id: 5),
                Release("0.1.573-beta", prerelease: true, id: 4),
                Release("0.1.565", id: 3),
                Release("1.5.0", id: 2),
            };
            foreach (string channel in new[] { "stable", "beta", "alpha" }) {
                Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, channel, "0.1.565-fork.2"));
                Assert.Null(UpdaterViewModel.SelectNewestRelease(releases, channel, "0.0.0.0"));
            }
        }

        static List<UpdaterViewModel.GithubRelease> ReadReleases(string name) {
            string path = Path.Join(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Files", "Updater", name);
            var releases = Json.Deserialize<List<UpdaterViewModel.GithubRelease>>(File.ReadAllText(path));
            Assert.NotNull(releases);
            return releases;
        }
    }
}
