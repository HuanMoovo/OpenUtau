using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using OpenUtau.App.ViewModels;
using Serilog;

namespace OpenUtau.App.Views {
    public partial class UpdaterDialog : Window {
        public readonly UpdaterViewModel ViewModel;
        public UpdaterDialog() {
            InitializeComponent();
            DataContext = ViewModel = new UpdaterViewModel();
        }

        void OnClosing(object sender, WindowClosingEventArgs e) {
            ViewModel.OnClosing();
        }

        /// <summary>Whether CheckForUpdate runs. UI tests turn it off to stay offline.</summary>
        public static bool CheckForUpdateEnabled { get; set; } = true;

        public static void CheckForUpdate(Action<Window> showDialog, Action closeApplication, TaskScheduler scheduler) {
            if (!CheckForUpdateEnabled) {
                return;
            }
            // What counts as an update is decided by our own release list and version
            // comparison - a build that cannot read its own version, or that is not older
            // than any of our releases, never gets here. Sparkle is only asked whether it
            // can install the update, which the dialog does when it opens.
            Task.Run(async () => await UpdaterViewModel.IsUpdateAvailableAsync()).ContinueWith(t => {
                if (t.IsCompletedSuccessfully && t.Result) {
                    var dialog = new UpdaterDialog();
                    dialog.ViewModel.CloseApplication = closeApplication;
                    showDialog.Invoke(dialog);
                }
                if (t.IsFaulted) {
                    Log.Error(t.Exception, "Failed to check for update");
                }
            }, scheduler).ContinueWith((t2, _) => {
                if (t2.IsFaulted) {
                    Log.Error(t2.Exception, "Failed to check for update");
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
