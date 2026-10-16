using System;
using System.Collections.Generic;
using System.IO;

namespace OpenUtau.Core.Util {
    /// <summary>
    /// Resolves the portrait image of a singer whose config does not name one.
    ///
    /// A singer only had a portrait when its character.yaml wrote a "portrait:" key, so a bank
    /// that ships nothing but an icon (character.txt "image=avatar.jpg") never showed anything
    /// behind the piano roll. The chain resolved here is:
    ///
    ///   portrait: in character.yaml     (absolute precedence; used as-is, never resolved here)
    ///   portrait.*                      (in the singer folder, or inside its archive)
    ///   character.*
    ///   立绘.*
    ///   the singer's own icon: character.yaml "icon:" or character.txt "image=", e.g. avatar.jpg
    ///   nothing, exactly as before
    ///
    /// Names are matched case-insensitively and only .png .jpg .jpeg .webp .bmp count, in that
    /// order of priority inside a name. Only the folder (or archive) itself is searched: a
    /// portrait conventionally sits next to character.txt.
    /// </summary>
    public static class PortraitFinder {
        /// <summary>Conventional portrait file names, in priority order.</summary>
        public static readonly string[] BaseNames = { "portrait", "character", "立绘" };
        /// <summary>Extensions a portrait may have, in priority order.</summary>
        public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

        /// <summary>
        /// Name of the image to use inside <paramref name="folder"/>, relative to that folder,
        /// or null when the folder has no usable image. <paramref name="image"/> is the singer's
        /// own icon, used as the last resort.
        /// </summary>
        public static string? FindInFolder(string? folder, string? image) {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
                return null;
            }
            // Exact names first: a handful of stats, no directory listing.
            foreach (var name in CandidateNames()) {
                if (File.Exists(Path.Combine(folder, name))) {
                    return name;
                }
            }
            if (!string.IsNullOrWhiteSpace(image) && File.Exists(Path.Combine(folder, image))) {
                return image;
            }
            // Nothing matched by exact name. One listing retries the same order
            // case-insensitively, so Portrait.PNG or AVATAR.JPG are found too.
            var files = ListImageFiles(folder);
            foreach (var name in CandidateNames()) {
                if (files.TryGetValue(name, out var actual)) {
                    return actual;
                }
            }
            // An icon in a subfolder ("icons/avatar.png") is matched by its full relative path.
            if (!string.IsNullOrWhiteSpace(image) && Path.GetFileName(image) == image
                && files.TryGetValue(image, out var icon)) {
                return icon;
            }
            return null;
        }

        /// <summary>
        /// Key of the image to use inside an archive (a vogen .vogeon, or any zip), or null.
        /// <paramref name="image"/> is the singer's own icon inside the same archive.
        /// </summary>
        public static string? FindInEntries(IEnumerable<string> entryNames, string? image) {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in entryNames) {
                if (string.IsNullOrEmpty(key)) {
                    continue;
                }
                keys.Add(key);
                var name = Path.GetFileName(key);
                if (ExtensionRank(name) >= 0) {
                    byName.TryAdd(name, key);
                }
            }
            foreach (var name in CandidateNames()) {
                if (byName.TryGetValue(name, out var key)) {
                    return key;
                }
            }
            if (!string.IsNullOrWhiteSpace(image)) {
                if (keys.Contains(image)) {
                    return image;
                }
                if (Path.GetFileName(image) == image && byName.TryGetValue(image, out var icon)) {
                    return icon;
                }
            }
            return null;
        }

        /// <summary>Candidate file names in priority order: portrait.png, portrait.jpg, ... 立绘.bmp.</summary>
        static IEnumerable<string> CandidateNames() {
            foreach (var baseName in BaseNames) {
                foreach (var ext in Extensions) {
                    yield return baseName + ext;
                }
            }
        }

        /// <summary>The folder's image files by name, matched case-insensitively.</summary>
        static Dictionary<string, string> ListImageFiles(string folder) {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)) {
                var name = Path.GetFileName(path);
                if (ExtensionRank(name) >= 0) {
                    files.TryAdd(name, name);
                }
            }
            return files;
        }

        /// <summary>Index of the extension in <see cref="Extensions"/>, or -1 when it is not a supported image.</summary>
        static int ExtensionRank(string nameOrPath) {
            var ext = Path.GetExtension(nameOrPath);
            for (int i = 0; i < Extensions.Length; i++) {
                if (string.Equals(ext, Extensions[i], StringComparison.OrdinalIgnoreCase)) {
                    return i;
                }
            }
            return -1;
        }
    }
}
