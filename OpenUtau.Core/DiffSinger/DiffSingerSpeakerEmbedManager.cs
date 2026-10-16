using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.ML.OnnxRuntime.Tensors;
using NumSharp;
using Serilog;

using OpenUtau.Core.Render;

namespace OpenUtau.Core.DiffSinger
{
    public class DiffSingerSpeakerEmbedManager
    {
        DsConfig dsConfig;
        string rootPath;
        public NDArray speakerEmbeds = null;
        const string VoiceColorHeader = DiffSingerUtils.VoiceColorHeader;
        // The suffix lookup runs once per segment of every rendered phrase (three times per phrase:
        // variance, pitch and acoustic), so its linear scans over dsConfig.speakers are cached.
        // The config is loaded once per singer and its speakers are not edited afterwards; the cache
        // is dropped if the list instance is ever replaced.
        readonly Dictionary<string, int> speakerIndexBySuffix = new Dictionary<string, int>();
        List<string> speakerIndexCacheFor = null;

        public DiffSingerSpeakerEmbedManager(DsConfig dsConfig, string rootPath) {
            this.dsConfig = dsConfig;
            this.rootPath = rootPath;
        }
        public NDArray loadSpeakerEmbed(string speaker) {
            string path = Path.Join(rootPath, speaker + ".emb");
            if(File.Exists(path)) {
                using var reader = new BinaryReader(File.OpenRead(path));
                return np.array<float>(Enumerable.Range(0, dsConfig.hiddenSize)
                    .Select(i => reader.ReadSingle()));
            } else {
                throw new Exception($"Speaker embed file {path} not found");
            }
        }

        public NDArray getSpeakerEmbeds() {
            if(speakerEmbeds == null) {
                if(dsConfig.speakers == null) {
                    return null;
                } else {
                    var embeds = np.zeros<float>(dsConfig.hiddenSize, dsConfig.speakers.Count);
                    foreach(var spkId in Enumerable.Range(0, dsConfig.speakers.Count)) {
                        embeds[":", spkId] = loadSpeakerEmbed(dsConfig.speakers[spkId]);
                    }
                    speakerEmbeds = embeds;
                }
            }
            return speakerEmbeds;
        }

        public bool IsVoiceColorCurve(string abbr, out int subBankId) {
            subBankId = 0;
            if (abbr.StartsWith(VoiceColorHeader) && int.TryParse(abbr.Substring(2), out subBankId)) {;
                subBankId -= 1;
                return true;
            } else {
                return false;
            }
        }

        public int getSpeakerIndexBySuffix(string suffix) {
            if (speakerIndexCacheFor != dsConfig.speakers) {
                speakerIndexBySuffix.Clear();
                speakerIndexCacheFor = dsConfig.speakers;
            }
            if (speakerIndexBySuffix.TryGetValue(suffix, out int cachedIndex)) {
                return cachedIndex;
            }
            var speakerIndex = dsConfig.speakers.IndexOf(suffix);
            if (speakerIndex < 0) {
                speakerIndex = dsConfig.speakers.FindIndex(s => {
                    var spSegs = s.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var sfSegs = suffix.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return sfSegs.Length <= spSegs.Length
                        && spSegs[^sfSegs.Length..].SequenceEqual(sfSegs);
                });
            }
            if (speakerIndex >= 0) {
                speakerIndexBySuffix[suffix] = speakerIndex;
                return speakerIndex;
            }
            if (dsConfig.speakers.Count == 0) {
                throw new InvalidOperationException(
                    "Subbanks are defined in character.yaml but \"speakers\" is empty in dsconfig.yaml.");
            }
            Log.Warning(
                $"Speaker suffix \"{suffix}\" not found in dsConfig.speakers, falling back to first speaker. " +
                $"Candidates: {string.Join(',', dsConfig.speakers)}.");
            speakerIndexBySuffix[suffix] = 0;
            return 0;
        }

        //used by phonemizer (duration model)
        public Tensor<float> PhraseSpeakerEmbedByPhone(string[] speakerByPhone){
            var hiddenSize = dsConfig.hiddenSize;
            var speakerEmbeds = getSpeakerEmbeds();
            var totalPhones = speakerByPhone.Length;
            NDArray spkCurves = np.zeros<float>(totalPhones, dsConfig.speakers.Count);
            foreach(int phoneId in Enumerable.Range(0,totalPhones)) {
                var spkId = getSpeakerIndexBySuffix(speakerByPhone[phoneId]);
                spkCurves[phoneId, spkId] = 1;
            }
            var spkEmbedResult = np.dot(spkCurves, speakerEmbeds.T);
            var spkEmbedTensor = new DenseTensor<float>(spkEmbedResult.ToArray<float>(), 
                new int[] { totalPhones, hiddenSize })
                .Reshape(new int[] { 1, totalPhones, hiddenSize });
            return spkEmbedTensor;
        }

        //used by variance, pitch and acoustic
        public Tensor<float> PhraseSpeakerEmbedByFrame(RenderPhrase phrase, IList<int> durations, float frameMs, int totalFrames, int headFrames, int tailFrames){
            var singer = phrase.singer;
            var hiddenSize = dsConfig.hiddenSize;
            var speakerEmbeds = getSpeakerEmbeds();
            var speakerCount = dsConfig.speakers.Count;
            //get default speaker for each padded segment
            var segments = DiffSingerUtils.PaddedSegments(phrase, frameMs, headFrames, tailFrames);
            int paddedFrames = 0;
            for (int i = 0; i < durations.Count; ++i) {
                paddedFrames += durations[i];
            }
            // Sized for the padded frames as well as the requested ones: the caller is expected to
            // pass durations summing to totalFrames, and the extra room keeps such a caller working
            // the way the list this replaces did.
            var defaultSpkByFrame = new int[Math.Max(totalFrames, paddedFrames)];
            var currentSpk = getSpeakerIndexBySuffix(phrase.phones[0].suffix);
            int defaultFrame = 0;
            for (int i = 0; i < segments.Count; ++i) {
                if (segments[i].PhoneIndex >= 0) {
                    currentSpk = getSpeakerIndexBySuffix(phrase.phones[segments[i].PhoneIndex].suffix);
                }
                for (int d = durations[i]; d > 0; --d) {
                    defaultSpkByFrame[defaultFrame++] = currentSpk;
                }
            }
            //get speaker curves
            //One flat row-major (frame, speaker) buffer, written in place: the NDArray slice
            //indexers this replaces allocated an index array plus a view for every curve write
            //and for every frame of the standardization below.
            var spkCurves = new float[totalFrames * speakerCount];
            foreach(var curve in phrase.curves) {
                if(IsVoiceColorCurve(curve.Item1,out int subBankId) && subBankId < singer.Subbanks.Count) {
                    var spkId = getSpeakerIndexBySuffix(singer.Subbanks[subBankId].Suffix);
                    var spkCurve = DiffSingerUtils.SampleCurve(phrase, curve.Item2, 0,
                        frameMs, totalFrames, headFrames, tailFrames, x => x * 0.01f)
                        .Select(f => (float)f).ToArray();
                    for (int frameId = 0; frameId < totalFrames; ++frameId) {
                        spkCurves[frameId * speakerCount + spkId] += spkCurve[frameId];
                    }
                }
            }
            NormalizeSpeakerCurves(spkCurves, totalFrames, speakerCount, defaultSpkByFrame);
            // Degenerate shapes keep the row-wise construction, which is what callers saw before.
            var spkCurvesNd = totalFrames == 0 || speakerCount == 0
                ? np.zeros<float>(totalFrames, speakerCount)
                : SpeakerCurvesToMatrix(spkCurves, totalFrames, speakerCount);
            var spkEmbedResult = np.dot(spkCurvesNd, speakerEmbeds.T);
            var spkEmbedTensor = new DenseTensor<float>(spkEmbedResult.ToArray<float>(), 
                new int[] { totalFrames, hiddenSize })
                .Reshape(new int[] { 1, totalFrames, hiddenSize });
            return spkEmbedTensor;
        }

        /// <summary>
        /// Standardizes the per-frame speaker curves in place: frames whose curves sum above 1 are
        /// rescaled to 1, frames below 1 give the remainder to their default speaker.
        /// Same arithmetic as the row-wise version this replaces, on the same values in the same
        /// order: float additions per curve, the sum accumulated in a double exactly like
        /// <c>Enumerable.Sum(float[])</c> does, then a float division or a float remainder add.
        /// </summary>
        internal static void NormalizeSpeakerCurves(float[] spkCurves, int totalFrames, int speakerCount, int[] defaultSpkByFrame) {
            for(int frameId = 0; frameId < totalFrames; ++frameId) {
                int rowStart = frameId * speakerCount;
                double spkSumDouble = 0;
                for (int spkId = 0; spkId < speakerCount; ++spkId) {
                    spkSumDouble += spkCurves[rowStart + spkId];
                }
                float spkSum = (float)spkSumDouble;
                if (spkSum > 1) {
                    for (int spkId = 0; spkId < speakerCount; ++spkId) {
                        spkCurves[rowStart + spkId] /= spkSum;
                    }
                } else {
                    spkCurves[rowStart + defaultSpkByFrame[frameId]] += 1 - spkSum;
                }
            }
        }

        /// <summary>
        /// The flat curve buffer as the (totalFrames, speakerCount) matrix the np.dot below wants.
        /// NumSharp is row-major, so <c>matrix[frame, spkId]</c> is <c>spkCurves[frame * speakerCount + spkId]</c>.
        /// </summary>
        internal static NDArray SpeakerCurvesToMatrix(float[] spkCurves, int totalFrames, int speakerCount) {
            return np.array<float>(spkCurves).reshape(new Shape(totalFrames, speakerCount));
        }
    }
}
