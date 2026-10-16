using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.App.ViewModels {
    /// <summary>
    /// Session-wide cache of decoded singer portraits - the model image the track UI shows - shared
    /// by the compact track list rows and the inspector's banner, so the same image is decoded once
    /// for every (portrait, height) whatever asks for it. Portraits are read and decoded on a single
    /// background worker, never on the UI thread.
    /// The source is USinger.LoadPortrait, the same lookup the piano roll background used: a bank's
    /// character.yaml "portrait:", or the conventional file names and the bank's own image.
    /// </summary>
    public static class SingerPortraitCache {
        // Images taller than this are scaled down on load when the voicebank declares no height of
        // its own, so a huge portrait is never kept at full size.
        const int MaxHeightWithoutVoicebankHeight = 800;

        class Entry {
            public USinger Singer = null!;
            // The portrait path and the height the bitmap was decoded at.
            public string Source = null!;
            public int Height;
            public Bitmap? Bitmap;
            public bool Pending;
            public List<Action<Bitmap?>>? Callbacks;

            /// <summary>
            /// False when the singer's portrait changed while this entry was decoding, so its
            /// bitmap belongs to nobody and must not reach a view.
            /// </summary>
            public bool StillCurrent() =>
                Singer.Found && Singer.Portrait == Source && Singer.PortraitHeight == Height;
        }

        // Keyed by instance: a singer rescan creates new USinger objects and the old entries are
        // collected with them.
        static readonly ConditionalWeakTable<USinger, Entry> entries = new ConditionalWeakTable<USinger, Entry>();
        static readonly ConcurrentQueue<(Entry entry, USinger singer, string source, int height)> queue =
            new ConcurrentQueue<(Entry, USinger, string, int)>();
        static int draining;

        /// <summary>
        /// Returns the cached portrait of the singer. If it is not decoded yet, returns null, queues
        /// the decoding and later calls onLoaded on the UI thread (with null if there is no usable
        /// image). Must be called on the UI thread. Returned bitmaps are shared and must not be
        /// disposed.
        /// </summary>
        public static Bitmap? Get(USinger singer, Action<Bitmap?>? onLoaded = null) {
            Dispatcher.UIThread.VerifyAccess();
            if (!singer.Found) {
                return null;
            }
            string? source = singer.Portrait;
            if (string.IsNullOrEmpty(source)) {
                return null;
            }
            int height = singer.PortraitHeight;
            if (entries.TryGetValue(singer, out var entry) &&
                entry.Source == source && entry.Height == height) {
                if (!entry.Pending) {
                    return entry.Bitmap;
                }
                if (onLoaded != null) {
                    entry.Callbacks!.Add(onLoaded);
                }
                return null;
            }
            entry = new Entry {
                Singer = singer,
                Source = source,
                Height = height,
                Pending = true,
                Callbacks = new List<Action<Bitmap?>>(),
            };
            if (onLoaded != null) {
                entry.Callbacks.Add(onLoaded);
            }
            entries.AddOrUpdate(singer, entry);
            queue.Enqueue((entry, singer, source, height));
            if (Interlocked.CompareExchange(ref draining, 1, 0) == 0) {
                Task.Run(Drain);
            }
            return null;
        }

        static void Drain() {
            while (true) {
                while (queue.TryDequeue(out var item)) {
                    var bitmap = Decode(item.singer, item.source, item.height);
                    var entry = item.entry;
                    Dispatcher.UIThread.Post(() => Complete(entry, bitmap));
                }
                Interlocked.Exchange(ref draining, 0);
                // Something may have been queued after the last dequeue but before draining was reset.
                if (queue.IsEmpty || Interlocked.CompareExchange(ref draining, 1, 0) != 0) {
                    return;
                }
            }
        }

        static void Complete(Entry entry, Bitmap? bitmap) {
            entry.Pending = false;
            if (!entry.StillCurrent()) {
                // The singer's portrait changed while this decode ran: the bitmap was never shown
                // to anyone, so it is disposed here instead of handed to a binding.
                bitmap?.Dispose();
                entry.Bitmap = null;
                entry.Callbacks = null;
                return;
            }
            entry.Bitmap = bitmap;
            var callbacks = entry.Callbacks;
            entry.Callbacks = null;
            if (callbacks == null) {
                return;
            }
            foreach (var callback in callbacks) {
                try {
                    callback(bitmap);
                } catch (Exception e) {
                    Log.Error(e, "Failed to apply portrait.");
                }
            }
        }

        /// <summary>
        /// Reads and decodes the portrait off the UI thread. A height of 0 keeps the image's own
        /// size, except that images taller than 800px are scaled down to 800; a declared height is
        /// applied as is.
        /// </summary>
        static Bitmap? Decode(USinger singer, string source, int height) {
            try {
                var data = singer.LoadPortrait();
                if (data == null) {
                    return null;
                }
                using var stream = new MemoryStream(data);
                var decoded = new Bitmap(stream);
                // Bitmap.Size is in doubles; the target heights are whole pixels.
                double nativeHeight = decoded.Size.Height;
                int targetHeight = height != 0
                    ? height
                    : Math.Min((int)Math.Round(nativeHeight), MaxHeightWithoutVoicebankHeight);
                if (targetHeight == (int)Math.Round(nativeHeight)) {
                    return decoded;
                }
                int targetWidth = Math.Max(1, (int)Math.Round(targetHeight * decoded.Size.Width / nativeHeight));
                try {
                    return decoded.CreateScaledBitmap(new PixelSize(targetWidth, targetHeight));
                } finally {
                    decoded.Dispose();
                }
            } catch (Exception e) {
                Log.Error(e, $"Failed to decode portrait {source} of {singer.Name}.");
                return null;
            }
        }
    }
}
