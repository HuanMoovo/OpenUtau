using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media;
using NetSparkleUpdater;
using NetSparkleUpdater.AppCastHandlers;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.SignatureVerifiers;
using OpenUtau.Core;
using OpenUtau.Core.Util;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;
using Serilog;

namespace OpenUtau.App.ViewModels {
    public partial class UpdaterViewModel : ViewModelBase {
        public class GithubReleaseAsset {
            public string name = string.Empty;
            public string browser_download_url = string.Empty;
        }
        public class GithubRelease {
#pragma warning disable 0649
            public string html_url = string.Empty;
            public long id = long.MaxValue;
            public bool draft;
            public bool prerelease;
            public string tag_name = string.Empty;
            public string name = string.Empty;
            public GithubReleaseAsset[] assets = new GithubReleaseAsset[0];
#pragma warning restore 0649
        }

        /// <summary>An update we can offer: one of our releases, newer than this build.</summary>
        public class UpdatePlan {
            /// <summary>Version of the release, e.g. "0.1.565-fork.2".</summary>
            public string version = string.Empty;
            /// <summary>Our release page - where Update sends the user when the release has no appcast.</summary>
            public string releaseUrl = string.Empty;
            /// <summary>
            /// Appcast of the release, when it ships one. Only then can Sparkle download and
            /// swap the files in place.
            /// </summary>
            public string? appcastUrl;
        }

        /// <summary>The outcome of one update check.</summary>
        public class UpdateCheck {
            /// <summary>The update to offer, or null when this build is up to date.</summary>
            public UpdatePlan? plan;
            /// <summary>True when the check itself failed; then we know nothing about updates.</summary>
            public bool failed;
        }

        public string AppVersion => $"v{Core.Util.AppVersion.WithChannel}";
        public bool IsDarkMode => ThemeManager.IsDarkMode;
        [Reactive] public partial string UpdaterStatus { get; set; }
        [Reactive] public partial bool UpdateAvailable { get; set; }
        [Reactive] public partial FontWeight UpdateButtonFontWeight { get; set; }
        // Index into stable / beta / alpha, same order as the preferences dialog.
        [Reactive] public partial int Channel { get; set; }
        [Reactive] public partial bool UpdateAccepted { get; set; }
        public Action? CloseApplication { get; set; }

        private SparkleUpdater? sparkle;
        private UpdateInfo? updateInfo;
        private UpdatePlan? plan;
        // Bumped on every check so that a check still in flight when the user
        // switches channel does not overwrite the result of the newer one.
        private int checkGeneration;

        public UpdaterViewModel() {
            UpdaterStatus = string.Empty;
            UpdateAvailable = false;
            UpdateButtonFontWeight = FontWeight.Normal;
            Channel = Preferences.Default.Channel switch {
                "beta" => 1,
                "alpha" => 2,
                _ => 0
            };
            this.WhenAnyValue(vm => vm.Channel).Skip(1).Subscribe(channel => {
                Preferences.Default.Channel = channel switch {
                    1 => "beta",
                    2 => "alpha",
                    _ => "stable"
                };
                Preferences.Save();
                Init();
            });
            Init();
        }

        /// <summary>
        /// Checks our own repository for a release newer than this build.
        ///
        /// Our releases live in <see cref="VersionComparison.RepoSlug"/> and are tagged
        /// <c>v&lt;core&gt;-fork.&lt;n&gt;</c>; upstream's releases are deliberately never
        /// consulted, and <see cref="VersionComparison.IsNewerRelease"/> decides whether
        /// what came back is an update for a fork build at all.
        /// </summary>
        public static async Task<UpdateCheck> CheckAsync() {
            List<GithubRelease> releases;
            try {
                releases = await FetchReleasesAsync();
            } catch (Exception e) {
                Log.Error(e, "Failed to check for updates.");
                return new UpdateCheck { failed = true };
            }
            var release = SelectNewestRelease(releases, Preferences.Default.Channel, Core.Util.AppVersion.Display);
            return new UpdateCheck { plan = release == null ? null : PlanRelease(release) };
        }

        /// <summary>
        /// Whether the startup check should show the updater dialog, i.e. whether there
        /// is an update this user has not skipped. Never true for a build whose version
        /// cannot be read, or when the check itself failed.
        /// </summary>
        public static async Task<bool> IsUpdateAvailableAsync() {
            var check = await CheckAsync();
            if (check.failed || check.plan == null) {
                return false;
            }
            // SkipUpdate is stored as "<channel>:<version>". The bare
            // form without a channel prefix is the legacy format.
            string version = check.plan.version;
            return Preferences.Default.SkipUpdate != version &&
                Preferences.Default.SkipUpdate != $"{Preferences.Default.Channel}:{version}";
        }

        static async Task<List<GithubRelease>> FetchReleasesAsync() {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("User-Agent", "Other");
            client.Timeout = TimeSpan.FromSeconds(30);
            using var response = await client.GetAsync(VersionComparison.ReleasesApiUrl);
            response.EnsureSuccessStatusCode();
            string respBody = await response.Content.ReadAsStringAsync();
            return Json.Deserialize<List<GithubRelease>>(respBody) ?? new List<GithubRelease>();
        }

        // Channel filter for GitHub releases. Alpha builds are published as
        // prereleases tagged `<version>-alpha`, beta builds as prereleases
        // tagged `<version>-beta`. The alpha channel is the superset: it
        // sees both alpha and beta releases.
        public static bool IsReleaseForChannel(GithubRelease release, string channel) {
            if (release.draft) {
                return false;
            }
            return channel switch {
                "alpha" => release.prerelease,
                "beta" => release.prerelease && !release.tag_name.EndsWith("-alpha"),
                _ => !release.prerelease,
            };
        }

        /// <summary>
        /// Whether a release could update this build at all. Upstream splits its releases
        /// over the stable / beta / alpha channels and the channel preference picks between
        /// them; our fork publishes a single stream of <c>v&lt;core&gt;-fork.&lt;n&gt;</c>
        /// releases, so a fork release is a candidate on every channel.
        /// </summary>
        public static bool IsCandidateRelease(GithubRelease release, string channel) {
            if (release.draft) {
                return false;
            }
            return VersionComparison.IsForkRelease(release.tag_name) || IsReleaseForChannel(release, channel);
        }

        /// <summary>
        /// Newest release that is really newer than <paramref name="buildVersion"/>, or null
        /// when there is nothing to offer. No network and no preferences, so it can be tested
        /// on its own.
        /// </summary>
        public static GithubRelease? SelectNewestRelease(
            List<GithubRelease> releases, string channel, string? buildVersion) {
            return releases
                .Where(r => IsCandidateRelease(r, channel)
                    && VersionComparison.IsNewerRelease(r.tag_name, buildVersion))
                .Select(r => (release: r, version: VersionComparison.Parse(r.tag_name)))
                .OrderByDescending(x => x.version.Core)
                .ThenByDescending(x => x.version.ForkCounter)
                .ThenByDescending(x => x.release.id)
                .Select(x => x.release)
                .FirstOrDefault();
        }

        /// <summary>
        /// What to offer for a release: its version, our release page, and its appcast when it
        /// has one. Null when there is nothing to point the user at.
        /// </summary>
        public static UpdatePlan? PlanRelease(GithubRelease release) {
            string? appcastUrl = SelectAppcast(release)?.browser_download_url;
            if (appcastUrl == null && string.IsNullOrEmpty(release.html_url)) {
                return null;
            }
            return new UpdatePlan {
                version = VersionComparison.StripTagPrefix(release.tag_name),
                releaseUrl = release.html_url,
                appcastUrl = appcastUrl,
            };
        }

        public static SparkleUpdater NewSparkleUpdater(string appcastUrl) {
            return new ZipUpdater(appcastUrl, new Ed25519Checker(SecurityMode.Unsafe)) {
                UIFactory = null,
                CheckServerFileName = false,
                RelaunchAfterUpdate = true,
                RelaunchAfterUpdateCommandPrefix = OS.IsLinux() ? "./" : string.Empty,
                AppCastHandler = new XMLAppCast() {
                    AppCastFilter = new DowngradableFilter()
                },
            };
        }

        static GithubReleaseAsset? SelectAppcast(GithubRelease release) {
            string suffix = PathManager.Inst.IsInstalled ? "-installer"
                            : PathManager.Inst.IsAppImage ? "-appimage"
                            : "";
            return release.assets
                .Where(a => a.name == $"appcast.{OS.GetUpdaterRid()}{suffix}.xml")
                .FirstOrDefault();
        }

        async void Init() {
            int generation = ++checkGeneration;
            sparkle?.Dispose();
            sparkle = null;
            updateInfo = null;
            plan = null;
            UpdateAvailable = false;
            UpdateButtonFontWeight = FontWeight.Normal;
            UpdaterStatus = ThemeManager.GetString("updater.status.checking");
            var check = await CheckAsync();
            if (generation != checkGeneration) {
                return;
            }
            if (check.failed) {
                UpdaterStatus = ThemeManager.GetString("updater.status.unknown");
                return;
            }
            plan = check.plan;
            if (plan == null) {
                UpdaterStatus = ThemeManager.GetString("updater.status.notavailable");
                return;
            }
            // Our own comparison decides that there is an update; the appcast only says
            // whether Sparkle can install it silently, and Sparkle cannot make sense of a
            // fork version string ("0.1.565-fork.2" is not a System.Version).
            var newSparkle = plan.appcastUrl == null ? null : NewSparkleUpdater(plan.appcastUrl);
            var newInfo = newSparkle == null ? null : await newSparkle.CheckForUpdatesQuietly();
            if (generation != checkGeneration) {
                newSparkle?.Dispose();
                return;
            }
            sparkle = newSparkle;
            updateInfo = newInfo;
            UpdateAvailable = true;
            UpdateButtonFontWeight = FontWeight.Bold;
            UpdaterStatus = string.Format(ThemeManager.GetString("updater.status.available"), plan.version);
        }

        public void OnGithub() {
            try {
                OS.OpenWeb(VersionComparison.RepoUrl);
            } catch (Exception e) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(e));
            }
        }

        public async void OnUpdate() {
            if (plan == null) {
                return;
            }
            if (sparkle == null || updateInfo == null || updateInfo.Updates.Count == 0) {
                // Our releases ship a plain zip with no appcast for Sparkle to install, so
                // send the user to our release page instead. The download-and-swap path
                // below is unchanged for releases that do carry an appcast.
                UpdateAvailable = false;
                UpdateAccepted = true;
                OpenReleasePage();
                return;
            }
            UpdateAvailable = false;
            UpdateAccepted = true;

            AppCastItem? downloadedItem = null;
            sparkle.CloseApplication += () => {
                Log.Information($"shutting down for update");
                CloseApplication?.Invoke();
                Log.Information($"shut down for update");
            };
            sparkle.DownloadStarted += (item, path) => {
                Log.Information($"download started {path}");
                downloadedItem = item;
            };
            sparkle.DownloadFinished += (item, path) => {
                Log.Information($"download finished {path}");
                // `item` is somehow null in this callback, likely a NetSparkle bug.
                item = item ?? downloadedItem;
                if (item == null) {
                    Log.Error("DownloadFinished unexpected null item.");
                } else {
                    sparkle.InstallUpdate(downloadedItem, path);
                }
            };
            sparkle.DownloadHadError += (item, path, e) => {
                Log.Error(e, $"download error {path}");
            };
            sparkle.DownloadMadeProgress += (sender, item, e) => {
                UpdaterStatus = $"{e.ProgressPercentage}%";
            };

            await sparkle.InitAndBeginDownload(updateInfo.Updates.First());
        }

        void OpenReleasePage() {
            try {
                string url = plan?.releaseUrl ?? VersionComparison.RepoUrl;
                if (string.IsNullOrEmpty(url)) {
                    url = VersionComparison.RepoUrl;
                }
                Log.Information($"Opening release page: {url}");
                OS.OpenWeb(url);
            } catch (Exception e) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(e));
            }
        }

        public void OnClosing() {
            if (!UpdateAccepted && UpdateAvailable && plan != null) {
                Log.Information($"Skipping update {plan.version}");
                // Skip is per-channel so that skipping e.g. an alpha build does
                // not suppress the same-target beta or stable release.
                Preferences.Default.SkipUpdate = $"{Preferences.Default.Channel}:{plan.version}";
                Preferences.Save();
            }
        }
    }

    // Force allow downgrading so that switching between beta and stable works.
    public class DowngradableFilter : IAppCastFilter {
        static bool Eq(int a, int b) {
            a = a == -1 ? 0 : a;
            b = b == -1 ? 0 : b;
            return a == b;
        }
        // Ambiguous version equal where 1.2 == 1.2.0 == 1.2.0.0.
        static bool Eq(Version a, Version b) {
            return Eq(a.Major, b.Major)
                && Eq(a.Minor, b.Minor)
                && Eq(a.Build, b.Build)
                && Eq(a.Revision, b.Revision);
        }
        public FilterResult GetFilteredAppCastItems(Version installed, List<AppCastItem> items) {
            items = items.Where(item => !Eq(new Version(item.Version), installed)).ToList();
            return new FilterResult(/*forceInstallOfLatestInFilteredList=*/true, items);
        }
    }

    public class ZipUpdater : SparkleUpdater {
        public ZipUpdater(string appcastUrl, ISignatureVerifier signatureVerifier) :
            base(appcastUrl, signatureVerifier) { }
        public ZipUpdater(string appcastUrl, ISignatureVerifier signatureVerifier, string referenceAssembly) :
            base(appcastUrl, signatureVerifier, referenceAssembly) { }
        public ZipUpdater(string appcastUrl, ISignatureVerifier signatureVerifier, string referenceAssembly, IUIFactory factory) :
            base(appcastUrl, signatureVerifier, referenceAssembly, factory) { }

        protected override string GetWindowsInstallerCommand(string downloadFilePath) {
            string installerExt = Path.GetExtension(downloadFilePath);
            if (DoExtensionsMatch(installerExt, ".exe")) {
                return $"\"{downloadFilePath}\"";
            }
            if (DoExtensionsMatch(installerExt, ".msi")) {
                return $"msiexec /i \"{downloadFilePath}\"";
            }
            if (DoExtensionsMatch(installerExt, ".msp")) {
                return $"msiexec /p \"{downloadFilePath}\"";
            }
            if (DoExtensionsMatch(installerExt, ".zip")) {
                string restart = RestartExecutablePath.TrimEnd('\\', '/');
                if (Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 17063) {
                    Log.Information("Starting update with tar.");
                    return $"tar -x -f \"{downloadFilePath}\" -C \"{restart}\"";
                }
                var unzipperPath = Path.Combine(Path.GetDirectoryName(downloadFilePath) ?? Path.GetTempPath(), "Unzipper.exe");
                File.WriteAllBytes(unzipperPath, Resources.Resources.Unzipper);
                Log.Information("Starting update with unzipper.");
                return $"{unzipperPath} \"{downloadFilePath}\" \"{restart}\"";
            }
            return downloadFilePath;
        }

        protected override async Task RunDownloadedInstaller(string downloadFilePath) {
            if (OS.IsLinux() && Path.GetExtension(downloadFilePath) == ".AppImage" && PathManager.Inst.IsAppImage) {
                string batchFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sh");
                string updateScript = $"""
                        COUNTER=0;
                        while ps -p {Environment.ProcessId} > /dev/null;
                            do sleep 1;
                            COUNTER=$((++COUNTER));
                            if [ $COUNTER -eq 90 ]
                            then
                                exit -1;
                            fi
                        done;

                        mv -f "{downloadFilePath}" "{PathManager.Inst.AppImagePath}"

                        chmod +x "{PathManager.Inst.AppImagePath}"

                        "{PathManager.Inst.AppImagePath}"
                    """;

                await File.WriteAllTextAsync(batchFilePath, updateScript.Replace("\r\n", "\n"));

                Exec($"chmod +x '{batchFilePath}' && '{batchFilePath}'", false);

                await QuitApplication();
            } else {
                await base.RunDownloadedInstaller(downloadFilePath);
            }
        }
    }
}
