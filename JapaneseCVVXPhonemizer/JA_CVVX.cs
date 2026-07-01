using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Classic;
using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core.G2p;
using OpenUtau.Core.Ustx;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Text.RegularExpressions;
using JA_SBP.Data;
using WanaKanaNet;
using System.Text;

namespace OpenUtau.Plugin.Builtin {
    [Phonemizer("Japanese CVVX Phonemizer", "JA CVVX", "Cadlaxa", language: "JA")]
    public class JA_CVVX : SyllableBasedPhonemizer {

        // Let the parent handle YAML Template logic securely
        protected override string YamlFileName => "ja-cvvx.yaml";
        protected override string YamlVersion => "1.2";
        protected override byte[] YamlTemplate => Resources.template;

        public Dictionary<string, List<string>> WanaKanaDictionary = new Dictionary<string, List<string>>();
        public Dictionary<string, List<string>> KanaToPhonemeDict = new Dictionary<string, List<string>>();
        private Dictionary<string, string> hiraganaToConsonantAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> defConsonants = new Dictionary<string, List<string>>();
        
        protected override string[] GetVowels() => "a i u e o".Split();
        protected override string[] GetConsonants() => "b by ch d dh f g gy h hy j k ky l ly m my n ny ng p py r ry s sh t ts th v w y z zh".Split();

        // For banks with missing vowels
        private readonly Dictionary<string, string> missingVphonemes = "ax=ah".Split(',')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2 && parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);
        private bool isMissingVPhonemes = false;

        // For banks with missing custom consonants
        private readonly Dictionary<string, string> missingCphonemes = "".Split(',')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2 && parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);
        private bool isMissingCPhonemes = false;

        // TIMIT symbols
        private readonly Dictionary<string, string> timitphonemes = "axh=ax,bcl=b,dcl=d,eng=ng,gcl=g,hv=hh,kcl=k,pcl=p,tcl=t".Split(',')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2 && parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);
        private bool isTimitPhonemes = false;
        
        private readonly string[] ccvException = { "ch", "dh", "dx", "fh", "gh", "hh", "jh", "kh", "ph", "ng", "sh", "th", "vh", "wh", "zh" };
        private readonly string[] RomajiException = { "a", "e", "i", "o", "u" };

        private List<KeyValuePair<string, List<string>>> sortedHiraToRomaKeys;
        private string[] sortedConsonants;
        private string[] sortedVowels;

        public override void SetSinger(USinger singer) {
            base.SetSinger(singer);
            if (this.singer == null || !this.singer.Loaded) return;

            string file = Path.Combine(singer.Location, YamlFileName);
            if (!File.Exists(file)) {
                file = Path.Combine(PluginDir, YamlFileName);
            }

            if (File.Exists(file)) {
                try {
                    // Extract specifically the custom structures utilizing the parent's tolerant deserializer
                    var data = TolerantDeserializer.Deserialize<CustomYAMLData>(File.ReadAllText(file));
    
                    WanaKanaDictionary.Clear();
                    KanaToPhonemeDict.Clear();

                    if (data.wanakana != null) {
                        foreach (var entry in data.wanakana) {
                            string key = string.Join("", entry.FromList);
                            string value = string.Join(" ", entry.ToList);
                            List<string> phonemeList = entry.FromList;

                            if (!WanaKanaDictionary.ContainsKey(key)) {
                                WanaKanaDictionary.Add(key, new List<string>());
                            }
                            if (!KanaToPhonemeDict.ContainsKey(value)) {
                                KanaToPhonemeDict.Add(value, phonemeList);
                            }
                            WanaKanaDictionary[key].Add(value);
                        }
                    }

                    defConsonants.Clear();
                    hiraganaToConsonantAliasMap.Clear();

                    if (data.consonantGroups != null) {
                        foreach (var entry in data.consonantGroups) {
                            string alias = entry.FromList.FirstOrDefault();
                            if (string.IsNullOrEmpty(alias)) continue;

                            if (!defConsonants.ContainsKey(alias)) {
                                defConsonants[alias] = new List<string>();
                            }

                            foreach (string member in entry.ToList) {
                                defConsonants[alias].Add(member);
                                if (!Regex.IsMatch(member, "^[a-zA-Z]+$")) {
                                    hiraganaToConsonantAliasMap[member] = alias;
                                }
                            }
                        }
                        UpdateHiraganaToConsonantMap();
                    }

                    foreach (var kvp in yamlFallbacks) {
                        missingVphonemes[kvp.Key] = kvp.Value;
                    }

                } catch (Exception ex) {
                    Log.Error(ex, $"Failed to parse custom JA_CVVX fields from {file}");
                }
            }
        }

        public class CustomYAMLData: YAMLData {
            public WanaKanaData[] wanakana { get; set; } = Array.Empty<WanaKanaData>();
            public ConsonantGroups[] consonantGroups { get; set; } = Array.Empty<ConsonantGroups>();
        }

        public class ConsonantGroups {
            public object group { get; set; }
            public object members { get; set; }

            public List<string> FromList {
                get {
                    if (group is string s) return new List<string> { s };
                    if (group is IEnumerable<object> list) return list.Select(x => x.ToString()).ToList();
                    return new List<string>();
                }
            }

            public List<string> ToList {
                get {
                    if (members is string s) return new List<string> { s };
                    if (members is IEnumerable<object> list) return list.Select(x => x.ToString()).ToList();
                    return new List<string>();
                }
            }
        }

        public class WanaKanaData {
            public object roma { get; set; }
            public object kana { get; set; }

            public List<string> FromList {
                get {
                    if (roma is string s) return new List<string> { s };
                    if (roma is IEnumerable<object> list) return list.Select(x => x.ToString()).ToList();
                    return new List<string>();
                }
            }

            public List<string> ToList {
                get {
                    if (kana is string s) return new List<string> { s };
                    if (kana is IEnumerable<object> list) return list.Select(x => x.ToString()).ToList();
                    return new List<string>();
                }
            }
        }

        // Parent handles dict replacements, rely on its inheritance 
        private string ReplacePhoneme(string phoneme, int tone) {
            if (dictionaryReplacements.TryGetValue(phoneme, out var replaced)) {
                return replaced;
            }
            if (HasOto(phoneme, tone) || HasOto(ValidateAlias(phoneme), tone)) {
                return phoneme;
            }
            return phoneme;
        }

        protected override string[] GetSymbols(Note note) {
            string[] original = base.GetSymbols(note);
            if (tails.Contains(note.lyric)) {
                return new string[] { note.lyric };
            }

            sortedHiraToRomaKeys = KanaToPhonemeDict.OrderByDescending(k => k.Key.Length).ToList();
            sortedConsonants = consonants.OrderByDescending(c => c.Length).ToArray();
            sortedVowels = vowels.OrderByDescending(v => v.Length).ToArray();

            if (original == null || original.Length == 0 || string.IsNullOrEmpty(original[0])) {
                string lyric = note.lyric.Trim().ToLowerInvariant();
                string romaji = "";

                if (Regex.IsMatch(lyric, @"[\p{IsHiragana}\p{IsKatakana}]+")) {
                    int ja = 0;
                    while (ja < lyric.Length) {
                        string match = null;

                        foreach (var kv in sortedHiraToRomaKeys) {
                            if (ja + kv.Key.Length <= lyric.Length && lyric.Substring(ja, kv.Key.Length) == kv.Key) {
                                match = kv.Key;
                                romaji += string.Join("", kv.Value); 
                                ja += kv.Key.Length;
                                break;
                            }
                        }

                        if (match == null) {
                            romaji += WanaKana.ToRomaji(lyric[ja].ToString());
                            ja++;
                        }
                    }
                } else {
                    romaji = lyric;
                }
                
                List<string> split = new List<string>();
                int ii = 0;
                while (ii < romaji.Length) {
                    string match = null;

                    foreach (var cons in sortedConsonants) {
                        if (romaji.Substring(ii).StartsWith(cons)) {
                            match = cons;
                            split.Add(cons);
                            ii += cons.Length;
                            break;
                        }
                    }

                    if (match != null) continue;

                    foreach (var vow in sortedVowels) {
                        if (romaji.Substring(ii).StartsWith(vow)) {
                            match = vow;
                            split.Add(vow);
                            ii += vow.Length;
                            break;
                        }
                    }

                    if (match == null) {
                        split.Add(romaji[ii].ToString());
                        ii++;
                    }
                }
                original = split.ToArray();
            }

            // Note: The previous massive Merging & Splitting Replacement loop has been removed 
            // since the parent class intrinsically applies all YAML regex replacements to the array 
            // returned by GetSymbols() via `ApplyReplacements()`.

            List<string> finalProcessedPhonemes = new List<string>();
            string[] tr = new[] { "tr" };
            string[] dr = new[] { "dr" };
            string[] wh = new[] { "wh" };
            string[] av_c = new[] { "al", "am", "an", "ang", "ar" };
            string[] ev_c = new[] { "el", "em", "en", "eng", "err" };
            string[] iv_c = new[] { "il", "im", "in", "ing", "ir" };
            string[] ov_c = new[] { "ol", "om", "on", "ong", "or" };
            string[] uv_c = new[] { "ul", "um", "un", "ung", "ur" };
            var consonatsV1 = new List<string> { "l", "m", "n", "r" };
            var consonatsV2 = new List<string> { "mm", "nn", "ng" };
            
            List<string> vowel3S = new List<string>();
            foreach (string V1 in vowels) {
                foreach (string C1 in consonatsV1) { vowel3S.Add($"{V1}{C1}"); }
            }
            
            List<string> vowel4S = new List<string>();
            foreach (string V1 in vowels) {
                foreach (string C1 in consonatsV2) { vowel4S.Add($"{V1}{C1}"); }
            }
            
            IEnumerable<string> phonemes = original;
            foreach (string s in phonemes) {
                switch (s) {
                    case var str when dr.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { "jh", s[1].ToString() });
                        break;
                    case var str when tr.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { "ch", s[1].ToString() });
                        break;
                    case var str when wh.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { "hh", s[1].ToString() });
                        break;
                    case var str when av_c.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { "aa", s[1].ToString() });
                        break;
                    case var str when ev_c.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { "eh", s[1].ToString() });
                        break;
                    case var str when iv_c.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { "iy", s[1].ToString() });
                        break;
                    case var str when ov_c.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { "ao", s[1].ToString() });
                        break;
                    case var str when uv_c.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { "uw", s[1].ToString() });
                        break;
                    case var str when vowel3S.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { s.Substring(0, 2), s[2].ToString() });
                        break;
                    case var str when vowel4S.Contains(str) && !HasOto($"{str}", note.tone) && !HasOto(ValidateAlias(str), note.tone):
                        finalProcessedPhonemes.AddRange(new string[] { s.Substring(0, 2), s.Substring(2, 2) });
                        break;
                    default:
                        finalProcessedPhonemes.Add(s);
                        break;
                }
            }
            return finalProcessedPhonemes.Where(p => p != null).ToArray();
        }

        private string ToHiragana(string alias, int tone) {
            var convertedHiragana = "";
            int i = 0;

            while (i < alias.Length) {
                bool foundMatch = false;

                var potentialRomajiKeys = WanaKanaDictionary
                    .Keys
                    .Where(key => alias.Length >= i + key.Length &&
                                alias.Substring(i, key.Length).Equals(key, StringComparison.Ordinal))
                    .OrderByDescending(key => key.Length)
                    .ToList();

                foreach (var romajiKey in potentialRomajiKeys) {
                    var kanaValues = WanaKanaDictionary[romajiKey];
                    string selectedKana = null;

                    foreach (var kana in kanaValues) {
                        var validatedKana = kana;
                        if (HasOto(validatedKana, tone) || HasOto(ValidateAlias(validatedKana), tone)) {
                            selectedKana = validatedKana;
                            break;
                        }
                    }

                    if (selectedKana == null) {
                        selectedKana = (kanaValues[0]);
                    }

                    convertedHiragana += selectedKana;
                    i += romajiKey.Length;
                    foundMatch = true;
                    break;
                }

                if (!foundMatch) {
                    convertedHiragana += alias[i];
                    i++;
                }
            }
            return convertedHiragana;
        }

        private void UpdateHiraganaToConsonantMap() {
            hiraganaToConsonantAliasMap.Clear();

            foreach (var entry in defConsonants) {
                string preferredConsonant = entry.Key; 
                List<string> members = entry.Value;

                foreach (string member in members) {
                    if (member != preferredConsonant) {
                        if (!string.IsNullOrEmpty(member) && !hiraganaToConsonantAliasMap.ContainsKey(member)) {
                            hiraganaToConsonantAliasMap[member] = preferredConsonant;
                        }
                    }
                }
            }
        }

        private string GetVC(string hiraganaSyllable, string baseConsonant) {
            if (string.IsNullOrWhiteSpace(hiraganaSyllable)) return baseConsonant;
            if (hiraganaToConsonantAliasMap.TryGetValue(hiraganaSyllable.Trim(), out string alias)) {
                return alias;
            }
            return baseConsonant;
        }

        protected override List<string> ProcessSyllable(Syllable syllable) {
            var replacedPrevV = ReplacePhoneme(syllable.prevV, syllable.tone);
            var prevV = string.IsNullOrEmpty(replacedPrevV) ? "" : replacedPrevV;
            string[] cc = syllable.cc.Select(c => ReplacePhoneme(c, syllable.tone)).ToArray();
            List<string> vowelsList = new List<string> { ReplacePhoneme(syllable.v, syllable.vowelTone) };
            string v = ReplacePhoneme(syllable.v, syllable.vowelTone);
            string basePhoneme;
            var phonemes = new List<string>();
            var lastC = cc.Length - 1;
            var firstC = 0;
            string[] CurrentWordCc = syllable.CurrentWordCc.Select(ReplacePhoneme).ToArray();
            string[] PreviousWordCc = syllable.PreviousWordCc.Select(ReplacePhoneme).ToArray();
            int prevWordConsonantsCount = syllable.prevWordConsonantsCount;

            foreach (var entry in missingVphonemes) {
                if (!HasOto(entry.Key, syllable.tone) && !HasOto(entry.Key, syllable.tone)) {
                    isMissingVPhonemes = true;
                    break;
                }
            }

            foreach (var entry in missingCphonemes) {
                if (!HasOto(entry.Key, syllable.tone) && !HasOto(entry.Value, syllable.tone)) {
                    isMissingCPhonemes = true;
                    break;
                }
            }

            foreach (var entry in timitphonemes) {
                if (!HasOto(entry.Key, syllable.tone) && !HasOto(entry.Value, syllable.tone)) {
                    isTimitPhonemes = true;
                    break;
                }
            }

            var hv = $"- {ToHiragana(v, syllable.vowelTone)}";
            
            if (syllable.IsStartingV) {
                if (HasOto(hv, syllable.vowelTone) || HasOto(ValidateAlias(hv), syllable.vowelTone)) {
                    basePhoneme = AliasFormat(ToHiragana(v, syllable.vowelTone), "startingV", syllable.vowelTone, "");
                } else if (HasOto(ToHiragana(v, syllable.vowelTone), syllable.vowelTone) || HasOto(ValidateAlias(ToHiragana(v, syllable.vowelTone)), syllable.vowelTone)) {
                    basePhoneme = AliasFormat(ToHiragana(v, syllable.vowelTone), "startingV", syllable.vowelTone, "");
                } else {
                    basePhoneme = AliasFormat(v, "startingV", syllable.vowelTone, "");
                }
            }
            else if (syllable.IsVV) {
                if (!CanMakeAliasExtension(syllable)) {
                    basePhoneme = $"{prevV} {ToHiragana(v, syllable.vowelTone)}";
                    int tone = syllable.vowelTone;
                    string hiraganaV = ToHiragana(v, tone);
                    string[] candidates = { $"{prevV} {hiraganaV}", $"{prevV} {v}" };
                    string[] candidates2 = { AliasFormat(hiraganaV, "vv_start", tone, ""), AliasFormat(v, "vv_start", tone, "") };
                    bool foundMatch = false;
                    for (int i = 0; i < candidates.Length; i++) {
                        string c1 = candidates[i];
                        string c2 = candidates2[i];

                        if (HasOto(c1, tone) || HasOto(ValidateAlias(c1), tone)) {
                            basePhoneme = c1;
                            foundMatch = true;
                            break;
                        } 
                        else if (HasOto(c2, tone) || HasOto(ValidateAlias(c2), tone)) {
                            basePhoneme = c2;
                            foundMatch = true;
                            break;
                        }
                    }
                    if (!foundMatch) {
                        if (HasOto(ToHiragana($"{prevV}{v}", syllable.vowelTone), syllable.vowelTone) || HasOto(ValidateAlias(ToHiragana($"{prevV}{v}", syllable.vowelTone)), syllable.vowelTone)) {
                            basePhoneme = ToHiragana($"{prevV}{v}", syllable.vowelTone);
                        } else if (HasOto($"{prevV}{v}", syllable.vowelTone) || HasOto(ValidateAlias($"{prevV}{v}"), syllable.vowelTone)) {
                            basePhoneme = $"{prevV}{v}";
                        } else {
                            basePhoneme = AliasFormat($"{v}", "vv_start", syllable.vowelTone, "");
                        }
                    }
                } else if (HasOto($"{ToHiragana(v, syllable.vowelTone)}", syllable.vowelTone) && HasOto(ValidateAlias($"{ToHiragana(v, syllable.vowelTone)}"), syllable.vowelTone)) {
                    basePhoneme = ToHiragana(v, syllable.vowelTone);
                } else if (HasOto($"{v}", syllable.vowelTone) && HasOto(ValidateAlias($"{v}"), syllable.vowelTone)) {
                    basePhoneme = v;
                } else {
                    basePhoneme = null;
                }
            } else if (syllable.IsStartingCVWithOneConsonant) {
                var rcv = $"- {cc[0]} {v}";
                var rcv1 = $"- {cc[0]}{v}";
                var crv = $"{cc[0]} {v}";
                var hcv = ToHiragana($"{cc[0]}{v}", syllable.tone);

                if (HasOto($"- {hcv}", syllable.vowelTone) || HasOto(ValidateAlias($"- {hcv}"), syllable.vowelTone)) {
                    basePhoneme = AliasFormat(hcv, "dynStart", syllable.vowelTone, "");
                } else if (HasOto(rcv, syllable.vowelTone) || HasOto(ValidateAlias(rcv), syllable.vowelTone) || (HasOto(rcv1, syllable.vowelTone) || HasOto(ValidateAlias(rcv1), syllable.vowelTone))) {
                    basePhoneme = AliasFormat($"{cc[0]} {v}", "dynStart", syllable.vowelTone, "");
                } else if (HasOto(hcv, syllable.vowelTone) || HasOto(ValidateAlias(hcv), syllable.vowelTone)) {
                    basePhoneme = AliasFormat(hcv, "dynMid", syllable.vowelTone, "");
                    TryAddPhoneme(phonemes, syllable.tone, $"- {cc[0]}", ValidateAlias(AliasFormat($"{cc[0]}", "cc_start", syllable.vowelTone, "")));
                } else if (HasOto(crv, syllable.vowelTone) || HasOto(ValidateAlias(crv), syllable.vowelTone)) {
                    basePhoneme = AliasFormat($"{cc[0]} {v}", "dynMid", syllable.vowelTone, "");
                    TryAddPhoneme(phonemes, syllable.tone, AliasFormat($"{cc[0]}", "cc_start", syllable.vowelTone, ""), ValidateAlias(AliasFormat($"{cc[0]}", "cc_start", syllable.vowelTone, "")));
                } else {
                    basePhoneme = AliasFormat($"{cc[0]} {v}", "dynMid", syllable.vowelTone, "");
                    TryAddPhoneme(phonemes, syllable.tone, AliasFormat($"{cc[0]}", "cc_start", syllable.vowelTone, ""), ValidateAlias(AliasFormat($"{cc[0]}", "cc_start", syllable.vowelTone, "")));
                }
            } else if (syllable.IsStartingCVWithMoreThanOneConsonant) {
                var rccv = $"- {string.Join("", cc)} {v}";
                var rccv1 = $"- {string.Join("", cc)}{v}";
                var crv = $"{cc.Last()} {v}";
                var crv1 = $"{cc.Last()}{v}";
                var ccv = $"{string.Join("", cc)} {v}";
                var ccv1 = $"{string.Join("", cc)}{v}";
                var hccv = ToHiragana($"{string.Join("", cc)}{v}", syllable.tone);
                var hcv = ToHiragana($"{cc.Last()}{v}", syllable.tone);

                if (HasOto(hccv, syllable.vowelTone) || HasOto(ValidateAlias(hccv), syllable.vowelTone) && !ccvException.Contains(cc[0])) {
                    basePhoneme = AliasFormat(hccv, "dynStart", syllable.vowelTone, "");
                    lastC = 0;
                } else if (HasOto(rccv, syllable.vowelTone) || HasOto(ValidateAlias(rccv), syllable.vowelTone) || HasOto(rccv1, syllable.vowelTone) || HasOto(ValidateAlias(rccv1), syllable.vowelTone) && !ccvException.Contains(cc[0])) {
                    basePhoneme = AliasFormat($"{string.Join("", cc)} {v}", "dynStart", syllable.vowelTone, "");
                    lastC = 0;
                } else {
                    if (HasOto(hccv, syllable.vowelTone) || HasOto(ValidateAlias(hccv), syllable.vowelTone) && !ccvException.Contains(cc[0])) {
                        basePhoneme = AliasFormat(hccv, "dynMid", syllable.vowelTone, "");
                        lastC = 0;
                    } else if (HasOto(ccv, syllable.vowelTone) || HasOto(ValidateAlias(ccv), syllable.vowelTone) || HasOto(ccv1, syllable.vowelTone) || HasOto(ValidateAlias(ccv1), syllable.vowelTone) && !ccvException.Contains(cc[0])) {
                        basePhoneme = AliasFormat($"{string.Join("", cc)} {v}", "dynMid", syllable.vowelTone, "");
                        lastC = 0;
                    } else if (HasOto(hcv, syllable.vowelTone) || HasOto(ValidateAlias(hcv), syllable.vowelTone)) {
                        basePhoneme = AliasFormat(hcv, "dynMid", syllable.vowelTone, "");
                    } else if (HasOto(crv, syllable.vowelTone) || HasOto(ValidateAlias(crv), syllable.vowelTone) || HasOto(crv1, syllable.vowelTone) || HasOto(ValidateAlias(crv1), syllable.vowelTone)) {
                        basePhoneme = AliasFormat($"{cc.Last()} {v}", "dynMid", syllable.vowelTone, "");
                    } else {
                        basePhoneme = AliasFormat($"{cc.Last()} {v}", "dynMid", syllable.vowelTone, "");
                    }

                    for (var i = cc.Length; i > 1; i--) {
                        if (!ccvException.Contains(cc[0])) {
                            if (TryAddPhoneme(phonemes, syllable.tone, AliasFormat($"{string.Join("", cc.Take(i))}", "cc_start", syllable.vowelTone, ""), ValidateAlias(AliasFormat($"{string.Join("", cc.Take(i))}", "cc_start", syllable.vowelTone, "")))) {
                                firstC = i - 1;
                            }
                        }
                        break;
                    }

                    if (phonemes.Count == 0) {
                        TryAddPhoneme(phonemes, syllable.tone, AliasFormat($"{cc[0]}", "cc_start", syllable.vowelTone, ""), ValidateAlias(AliasFormat($"{cc[0]}", "cc_start", syllable.vowelTone, "")));
                    }
                }

                for (var i = firstC; i < cc.Length - 1; i++) {
                    var cv = $"{cc.Last()} {v}";
                    if (CurrentWordCc.Length >= 2 && !ccvException.Contains(cc[i])) {
                        if (HasOto(hccv, syllable.vowelTone) || HasOto(ValidateAlias(hccv), syllable.vowelTone)) {
                            basePhoneme = AliasFormat(hccv, "dynMid", syllable.vowelTone, "");
                            lastC = i;
                            break;
                        }
                        else if (HasOto(ccv, syllable.vowelTone) || HasOto(ValidateAlias(ccv), syllable.vowelTone) || HasOto(ccv1, syllable.vowelTone) || HasOto(ValidateAlias(ccv1), syllable.vowelTone)) {
                            basePhoneme = AliasFormat($"{string.Join("", cc)} {v}", "dynMid", syllable.vowelTone, "");
                            lastC = i;
                            break;
                        }
                    } else if (CurrentWordCc.Length == 1 && PreviousWordCc.Length == 1) {
                        if (HasOto(hcv, syllable.vowelTone) || HasOto(ValidateAlias(hcv), syllable.vowelTone)) {
                            basePhoneme = AliasFormat(hcv, "dynMid", syllable.vowelTone, "");
                        } else {
                            basePhoneme = AliasFormat($"{cc.Last()} {v}", "dynMid", syllable.vowelTone, "");
                        }
                    }
                }
            } else { 
                var vcv = $"{prevV} {cc[0]}{v}";
                var vccv = $"{prevV} {string.Join("", cc)}{v}";
                var crv = $"{cc.Last()} {v}";
                var ccv = $"{string.Join("", cc)} {v}";
                var ccv1 = $"{string.Join("", cc)}{v}";
                var hccv = ToHiragana($"{string.Join("", cc)}{v}", syllable.tone);
                var hcv = ToHiragana($"{cc.Last()}{v}", syllable.tone);
                var hvcv = $"{prevV} {ToHiragana($"{cc[0]}{v}", syllable.tone)}";
                var hvccv = $"{prevV} {ToHiragana($"{string.Join("", cc)}{v}", syllable.tone)}";

                if (syllable.IsVCVWithOneConsonant && (HasOto(hvcv, syllable.vowelTone) || HasOto(ValidateAlias(hvcv), syllable.vowelTone)) && prevWordConsonantsCount == 0 && CurrentWordCc.Length == 1) {
                    basePhoneme = hvcv;
                } else if (syllable.IsVCVWithOneConsonant && (HasOto(vcv, syllable.vowelTone) || HasOto(ValidateAlias(vcv), syllable.vowelTone)) && prevWordConsonantsCount == 0 && CurrentWordCc.Length == 1) {
                    basePhoneme = vcv;
                } else if (syllable.IsVCVWithMoreThanOneConsonant && (HasOto(hvccv, syllable.vowelTone) || HasOto(ValidateAlias(hvccv), syllable.vowelTone) && prevWordConsonantsCount == 0)) {
                    basePhoneme = hvccv;
                    lastC = 0;
                } else if (syllable.IsVCVWithMoreThanOneConsonant && (HasOto(vccv, syllable.vowelTone) || HasOto(ValidateAlias(vccv), syllable.vowelTone) && prevWordConsonantsCount == 0)) {
                    basePhoneme = vccv;
                    lastC = 0;
                } else {
                    var cv = $"{cc.Last()}{v}";
                    
                    if (HasOto(hcv, syllable.vowelTone) || HasOto(ValidateAlias(hcv), syllable.vowelTone)) {
                        basePhoneme = AliasFormat(hcv, "dynMid", syllable.vowelTone, "");
                    } else if (HasOto(crv, syllable.vowelTone) || HasOto(ValidateAlias(crv), syllable.vowelTone) || HasOto(cv, syllable.vowelTone) || HasOto(ValidateAlias(cv), syllable.vowelTone)) {
                        basePhoneme = AliasFormat($"{cc.Last()} {v}", "dynMid", syllable.vowelTone, "");
                    } else {
                        basePhoneme = AliasFormat($"{cc.Last()} {v}", "dynMid", syllable.vowelTone, "");
                    }

                    for (var i = firstC; i < cc.Length - 1; i++) {
                        if (CurrentWordCc.Length >= 2 && !ccvException.Contains(cc[i])) {
                            if (HasOto(hccv, syllable.vowelTone) || HasOto(ValidateAlias(hccv), syllable.vowelTone)) {
                                basePhoneme = AliasFormat(hccv, "dynMid", syllable.vowelTone, "");
                                lastC = i;
                                break;
                            } else if (HasOto(ccv, syllable.vowelTone) || HasOto(ValidateAlias(ccv), syllable.vowelTone) || HasOto(ccv1, syllable.vowelTone) || HasOto(ValidateAlias(ccv1), syllable.vowelTone)) {
                                basePhoneme = AliasFormat($"{string.Join("", cc)} {v}", "dynMid", syllable.vowelTone, "");
                                lastC = i;
                                break;
                            }
                        } else if (CurrentWordCc.Length == 1 && PreviousWordCc.Length == 1) {
                            if (HasOto(hcv, syllable.vowelTone) || HasOto(ValidateAlias(hcv), syllable.vowelTone)) {
                                basePhoneme = AliasFormat(hcv, "dynMid", syllable.vowelTone, "");
                            } else {
                                basePhoneme = AliasFormat($"{cc.Last()} {v}", "dynMid", syllable.vowelTone, "");
                            }
                        }
                    }

                    if (basePhoneme.Contains("-")) {
                        for (int clusterLength = 3; clusterLength >= 2; clusterLength--) {
                            if (clusterLength > cc.Length) continue;

                            var cluster = new string[clusterLength];
                            Array.Copy(cc, 0, cluster, 0, clusterLength);

                            var consonantPatterns = new List<string>();

                            if (clusterLength >= 3) {
                                consonantPatterns.Add($"{cluster[0]} {cluster[1]}{cluster[2]}");
                                consonantPatterns.Add($"{cluster[0]}{cluster[1]} {cluster[2]}");
                                consonantPatterns.Add($"{cluster[0]} {cluster[1]} {cluster[2]}");
                            } else if (clusterLength == 2) {
                                consonantPatterns.Add($"{cluster[0]} {cluster[1]}");
                                consonantPatterns.Add($"{cluster[0]}{cluster[1]}");
                            }

                            foreach (var consPattern in consonantPatterns) {
                                string[] endPatterns = { "-", $" -" };
                                foreach (var end in endPatterns) {
                                    string endingcc = $"{consPattern}{end}";

                                    if (HasOto(endingcc, syllable.tone)) {
                                        basePhoneme = endingcc;
                                        lastC = 0;
                                        goto FoundMatch;
                                    }
                                }
                            }
                        }
                    }

                    FoundMatch:;

                    for (var i = lastC + 1; i >= 0; i--) {
                        var vcc = $"{prevV} {string.Join("", cc.Take(2))}";
                        var vc = $"{prevV} {cc[0]}";
                        bool CCV = false;
                        
                        if (CurrentWordCc.Length >= 2 && !ccvException.Contains(cc[0])) {
                            if (HasOto(AliasFormat($"{string.Join("", cc)} {v}", "dynMid", syllable.vowelTone, ""), syllable.vowelTone)) {
                                CCV = true;
                            }
                        }

                        string preferredC = GetVC(ToHiragana($"{string.Join("", cc)}{v}", syllable.tone), cc[0]);
                        var hvc = $"{prevV} {preferredC}";

                        if (i == 0 && !HasOto(vc, syllable.tone)) {
                            break;
                        } else if ((HasOto(vcc, syllable.tone) || HasOto(ValidateAlias(vcc), syllable.tone)) && CCV) {
                            phonemes.Add(vcc);
                            firstC = 1;
                            break;
                        } else if (HasOto(hvc, syllable.tone) || HasOto(ValidateAlias(hvc), syllable.tone)) {
                            phonemes.Add(hvc);
                            break;
                        } else if (HasOto(vc, syllable.tone) || HasOto(ValidateAlias(vc), syllable.tone)) {
                            phonemes.Add(vc);
                            break;
                        }
                    }
                }
            }

            for (var i = firstC; i < lastC; i++) {
                var cc1 = $"{string.Join(" ", cc.Skip(i))}";
                if (!HasOto(cc1, syllable.tone)) cc1 = ValidateAlias(cc1);
                
                if (!HasOto(cc1, syllable.tone)) cc1 = $"{cc[i]} {cc[i + 1]}";
                if (!HasOto(cc1, syllable.tone)) cc1 = ValidateAlias(cc1);
                
                if (!HasOto(cc1, syllable.tone) || (!HasOto(ValidateAlias(cc1), syllable.tone) && !HasOto($"{cc[i]} {cc[i + 1]}", syllable.tone))) {
                    cc1 = AliasFormat($"{cc[i]}", "cc_endB", syllable.vowelTone, "");
                }
                
                if (!HasOto(cc1, syllable.tone)) cc1 = ValidateAlias(cc1);
                
                if (HasOto($"{cc[i]} {string.Join("", cc.Skip(i + 1))}", syllable.tone)) {
                    cc1 = $"{cc[i]} {string.Join("", cc.Skip(i + 1))}";
                    lastC = i;
                }
                if (HasOto($"{cc[i]} {string.Join(" ", cc.Skip(i + 1))}", syllable.tone)) {
                    cc1 = $"{cc[i]} {string.Join(" ", cc.Skip(i + 1))}";
                    lastC = i;
                }
                if (HasOto($"{cc[i]}{string.Join("", cc.Skip(i + 1))}", syllable.tone)) {
                    cc1 = $"{cc[i]}{string.Join("", cc.Skip(i + 1))}";
                    lastC = i;
                }
                if (HasOto($"{cc[i]}{string.Join(" ", cc.Skip(i + 1))}", syllable.tone)) {
                    cc1 = $"{cc[i]}{string.Join(" ", cc.Skip(i + 1))}";
                    lastC = i;
                }

                if (i + 1 < lastC) {
                    if (!HasOto(cc1, syllable.tone)) cc1 = ValidateAlias(cc1);
                    if (!HasOto(cc1, syllable.tone)) cc1 = $"{cc[i]} {cc[i + 1]}";
                    if (!HasOto(cc1, syllable.tone)) cc1 = ValidateAlias(cc1);
                    
                    if (!HasOto(cc1, syllable.tone) || (!HasOto(ValidateAlias(cc1), syllable.tone) && !HasOto($"{cc[i]} {cc[i + 1]}", syllable.tone))) {
                        cc1 = AliasFormat($"{cc[i]}", "cc_endB", syllable.vowelTone, "");
                    }
                    if (!HasOto(cc1, syllable.tone)) cc1 = ValidateAlias(cc1);
                    
                    if (HasOto($"{cc[i]} {string.Join("", cc.Skip(i + 1))}", syllable.tone)) {
                        cc1 = $"{cc[i]} {string.Join("", cc.Skip(i + 1))}";
                        lastC = i;
                    }
                    if (HasOto($"{cc[i]} {string.Join(" ", cc.Skip(i + 1))}", syllable.tone)) {
                        cc1 = $"{cc[i]} {string.Join(" ", cc.Skip(i + 1))}";
                        lastC = i;
                    }
                    if (HasOto($"{cc[i]}{string.Join("", cc.Skip(i + 1))}", syllable.tone)) {
                        cc1 = $"{cc[i]}{string.Join("", cc.Skip(i + 1))}";
                        lastC = i;
                    }
                    if (HasOto($"{cc[i]}{string.Join(" ", cc.Skip(i + 1))}", syllable.tone)) {
                        cc1 = $"{cc[i]}{string.Join(" ", cc.Skip(i + 1))}";
                        lastC = i;
                    }

                    if (HasOto(cc1, syllable.tone) && HasOto(cc1, syllable.tone) && !cc1.Contains($"{string.Join("", cc.Skip(i))}")) {
                        phonemes.Add(cc1);
                    } else if (TryAddPhoneme(phonemes, syllable.tone, cc1, ValidateAlias(cc1))) {
                        if (cc1.Contains($"{string.Join(" ", cc.Skip(i + 1))}")) {
                            i++;
                        }
                    } else {
                        if (PreviousWordCc.Contains(cc1) == CurrentWordCc.Contains(cc1)) {
                            cc1 = ValidateAlias(cc1);
                        } else {
                            TryAddPhoneme(phonemes, syllable.tone, cc1, cc[i], ValidateAlias(cc[i]));
                        }
                    }
                } else {
                    TryAddPhoneme(phonemes, syllable.tone, cc1);
                }
            }

            phonemes.Add(basePhoneme);
            return phonemes;
        }

        protected override List<string> ProcessEnding(Ending ending) {
            string prevV = ReplacePhoneme(ending.prevV, ending.tone);
            string[] cc = ending.cc.Select(c => ReplacePhoneme(c, ending.tone)).ToArray();
            string v = ReplacePhoneme(ending.prevV, ending.tone);
            var phonemes = new List<string>();
            var lastC = cc.Length - 1;
            var firstC = 0;
            
            if (tails.Contains(ending.prevV)) {
                return new List<string>();
            }
            
            if (ending.IsEndingV) {
                var vR = $"{prevV} -";
                var vR1 = $"{prevV} R";
                var vR2 = $"{prevV}-";
                if (HasOto(vR, ending.tone) || HasOto(ValidateAlias(vR), ending.tone) || HasOto(vR1, ending.tone) || HasOto(ValidateAlias(vR1), ending.tone) || HasOto(vR2, ending.tone) || HasOto(ValidateAlias(vR2), ending.tone)) {
                    phonemes.Add(AliasFormat($"{prevV}", "ending", ending.tone, ""));
                }
            } else if (ending.IsEndingVCWithOneConsonant) {
                var vc = $"{prevV} {cc[0]}";
                var vcr = $"{prevV} {cc[0]}-";
                var vcr2 = $"{prevV}{cc[0]} -";
                var vcr3 = $"{prevV} {cc[0]} -";
                var vcr4 = $"{prevV}{cc[0]}-";
                if (!RomajiException.Contains(cc[0])) {
                    if (HasOto(vcr, ending.tone) && HasOto(ValidateAlias(vcr), ending.tone) || (HasOto(vcr2, ending.tone) && HasOto(ValidateAlias(vcr2), ending.tone))) {
                        phonemes.Add(AliasFormat($"{v} {cc[0]}", "dynEnd", ending.tone, ""));
                    } else if (HasOto(vcr3, ending.tone) && HasOto(ValidateAlias(vcr3), ending.tone)) {
                        phonemes.Add(vcr3);
                    } else if (HasOto(vcr4, ending.tone) && HasOto(ValidateAlias(vcr4), ending.tone)) {
                        phonemes.Add(vcr4);
                    } else if (HasOto(vc, ending.tone) && HasOto(ValidateAlias(vc), ending.tone)) {
                        phonemes.Add(vc);
                        if (vc.Contains(cc[0])) {
                            phonemes.Add(AliasFormat($"{cc[0]}", "ending", ending.tone, ""));
                        }
                    } else {
                        phonemes.Add(vc);
                        if (vc.Contains(cc[0])) {
                            phonemes.Add(AliasFormat($"{cc[0]}", "ending", ending.tone, ""));
                        }
                    }
                }
            } else {
                for (var i = lastC; i >= 0; i--) {
                    var vr = $"{v} -";
                    var vr1 = $"{v} R";
                    var vr2 = $"{v}-";
                    var vcc = $"{v} {string.Join("", cc.Take(2))}-";
                    var vcc2 = $"{v}{string.Join(" ", cc.Take(2))} -";
                    var vcc3 = $"{v}{string.Join(" ", cc.Take(2))}";
                    var vcc4 = $"{v} {string.Join("", cc.Take(2))}";
                    var vc = $"{v} {cc[0]}";
                    
                    if (!RomajiException.Contains(cc[0])) {
                        if (i == 0) {
                            if (HasOto(vr, ending.tone) || HasOto(ValidateAlias(vr), ending.tone) || HasOto(vr2, ending.tone) || HasOto(ValidateAlias(vr2), ending.tone) || HasOto(vr1, ending.tone) || HasOto(ValidateAlias(vr1), ending.tone) && !HasOto(vc, ending.tone)) {
                                phonemes.Add(AliasFormat($"{v}", "ending", ending.tone, ""));
                            }
                            break;
                        } else if (HasOto(vcc, ending.tone) && HasOto(ValidateAlias(vcc), ending.tone) && lastC == 1 && !ccvException.Contains(cc[0])) {
                            phonemes.Add(vcc);
                            firstC = 1;
                            break;
                        } else if (HasOto(vcc2, ending.tone) && HasOto(ValidateAlias(vcc2), ending.tone) && lastC == 1 && !ccvException.Contains(cc[0])) {
                            phonemes.Add(vcc2);
                            firstC = 1;
                            break;
                        } else if (HasOto(vcc3, ending.tone) && HasOto(ValidateAlias(vcc3), ending.tone) && !ccvException.Contains(cc[0])) {
                            phonemes.Add(vcc3);
                            if (vcc3.EndsWith(cc.Last()) && lastC == 1) {
                                if (consonants.Contains(cc.Last())) {
                                    phonemes.Add(AliasFormat($"{cc.Last()}", "ending", ending.tone, ""));
                                }
                            }
                            firstC = 1;
                            break;
                        } else if (HasOto(vcc4, ending.tone) && HasOto(ValidateAlias(vcc4), ending.tone) && !ccvException.Contains(cc[0])) {
                            phonemes.Add(vcc4);
                            if (vcc4.EndsWith(cc.Last()) && lastC == 1) {
                                if (consonants.Contains(cc.Last())) {
                                    phonemes.Add(AliasFormat($"{cc.Last()}", "ending", ending.tone, ""));
                                }
                            }
                            firstC = 1;
                            break;
                        } else if (!!HasOto(vcc, ending.tone) && !HasOto(ValidateAlias(vcc), ending.tone)
                                || !HasOto(vcc2, ending.tone) && HasOto(ValidateAlias(vcc2), ending.tone)
                                || !HasOto(vcc3, ending.tone) && HasOto(ValidateAlias(vcc3), ending.tone)
                                || !HasOto(vcc4, ending.tone) && HasOto(ValidateAlias(vcc4), ending.tone)) {
                            phonemes.Add(vc);
                            break;
                        } else {
                            phonemes.Add(vc);
                            break;
                        }
                    }
                }
                for (var i = firstC; i < lastC; i++) {
                    var cc1 = $"{cc[i]} {cc[i + 1]}";
                    if (i < cc.Length - 2) {
                        var cc2 = $"{cc[i + 1]} {cc[i + 2]}";
                        if (!HasOto(cc1, ending.tone)) cc1 = ValidateAlias(cc1);
                        if (!HasOto(cc2, ending.tone)) cc2 = ValidateAlias(cc2);

                        if (!HasOto(cc2, ending.tone) && !HasOto($"{cc[i + 1]} {cc[i + 2]}", ending.tone)) {
                            cc2 = AliasFormat($"{cc[i + 1]}", "cc_endB", ending.tone, "");
                        }
                        if (!HasOto(cc1, ending.tone)) cc1 = ValidateAlias(cc1);

                        if (HasOto(cc1, ending.tone) && (HasOto(cc2, ending.tone) || HasOto($"{cc[i + 1]} {cc[i + 2]}-", ending.tone) || HasOto(ValidateAlias($"{cc[i + 1]} {cc[i + 2]}-"), ending.tone))) {
                            phonemes.Add(cc1);
                        } else if ((HasOto(AliasFormat($"{cc[i]}", "cc_endB", ending.tone, ""), ending.tone) || HasOto(ValidateAlias(cc[i]), ending.tone) && (HasOto(cc2, ending.tone) || HasOto($"{cc[i + 1]} {cc[i + 2]}-", ending.tone) || HasOto(ValidateAlias($"{cc[i + 1]} {cc[i + 2]}-"), ending.tone)))) {
                            phonemes.Add(AliasFormat($"{cc[i]}", "cc_endB", ending.tone, ""));
                        } else if (TryAddPhoneme(phonemes, ending.tone, $"{cc[i + 1]} {cc[i + 2]}-", ValidateAlias($"{cc[i + 1]} {cc[i + 2]}-"))) {
                            i++;
                        } else if (TryAddPhoneme(phonemes, ending.tone, $"{cc[i + 1]}{cc[i + 2]}", ValidateAlias($"{cc[i + 1]}{cc[i + 2]}"))) {
                            i++;
                        } else if (TryAddPhoneme(phonemes, ending.tone, cc1, ValidateAlias(cc1))) {
                            i++;
                        } else if (!HasOto(cc1, ending.tone) && !HasOto($"{cc[i]} {cc[i + 1]}", ending.tone)) {
                            TryAddPhoneme(phonemes, ending.tone, AliasFormat($"{cc[i + 1]}", "cc_endB", ending.tone, ""));
                            i++;
                        } else {
                            TryAddPhoneme(phonemes, ending.tone, cc[i], ValidateAlias(cc[i]), $"{cc[i]} -", ValidateAlias($"{cc[i]} -"));
                            TryAddPhoneme(phonemes, ending.tone, cc[i + 1], ValidateAlias(cc[i + 1]), $"{cc[i + 1]} -", ValidateAlias($"{cc[i + 1]} -"));
                            TryAddPhoneme(phonemes, ending.tone, cc[i + 2], ValidateAlias(cc[i + 2]), $"{cc[i + 2]} -", ValidateAlias($"{cc[i + 2]} -"));
                            i++;
                        }
                    } else {
                        if (!HasOto(cc1, ending.tone)) cc1 = ValidateAlias(cc1);
                        if (!HasOto(cc1, ending.tone)) cc1 = $"{cc[i]} {cc[i + 1]}";
                        if (!HasOto(cc1, ending.tone)) cc1 = ValidateAlias(cc1);
                        
                        if (!HasOto(cc1, ending.tone) || !HasOto(ValidateAlias(cc1), ending.tone) && !HasOto($"{cc[i]} {cc[i + 1]}", ending.tone)) {
                            cc1 = AliasFormat($"{cc[i]}", "cc_endB", ending.tone, "");
                        }
                        if (!HasOto(cc1, ending.tone)) cc1 = ValidateAlias(cc1);
                        
                        if (TryAddPhoneme(phonemes, ending.tone, cc1, ValidateAlias(cc1))) {
                            TryAddPhoneme(phonemes, ending.tone, $"{cc[i + 1]} -", ValidateAlias($"{cc[i + 1]} -"), cc[i + 1], ValidateAlias(cc[i + 1]));
                            i++;
                        }
                    }
                }
            }
            return phonemes;
        }

        private string AliasFormat(string alias, string type, int tone, string prevV) {
            var aliasFormats = new Dictionary<string, string[]> {
                { "dynStart", new string[] { "" } },
                { "dynMid", new string[] { "" } },
                { "dynMid_vv", new string[] { "" } },
                { "dynEnd", new string[] { "" } },
                { "startingV", new string[] { "-", "- ", "_", "" } },
                { "vcEx", new string[] { $"{prevV} ", $"{prevV}" } },
                { "vv", new string[] { "", "_", "-", "- " } },
                { "vv_start", new string[] { "* ", "_", "", "" } },
                { "cv", new string[] { "-", "", "- ", "_" } },
                { "cvStart", new string[] { "-", "- ", "_" } },
                { "ending", new string[] { " R", "-", " -" } },
                { "ending_mix", new string[] { "-", " -", "R", " R", "_", "--" } },
                { "cc", new string[] { "", "-", "- ", "_" } },
                { "cc_start", new string[] { "- ", "-", "_" } },
                { "cc_end", new string[] { " -", "-", "" } },
                { "cc_inB", new string[] { "_", "-", "- " } },
                { "cc_endB", new string[] { "_", "-", " -" } },
                { "cc_mix", new string[] { " -", " R", "-", "", "_", "- ", "-" } },
                { "cc1_mix", new string[] { "", " -", "-", " R", "_", "- ", "-" } },
            };

            if (!aliasFormats.ContainsKey(type) && !type.Contains("dynamic")) {
                return alias;
            }

            if (type.Contains("dynStart")) {
                string consonant = "";
                string vowel = "";
                if (alias.Contains(" ")) {
                    var parts = alias.Split(' ');
                    consonant = parts[0];
                    vowel = parts[1];
                } else {
                    consonant = alias;
                }

                var dynamicVariations = new List<string> {
                    $"- {consonant}{vowel}",
                    $"- {consonant} {vowel}",
                    $"-{consonant} {vowel}",
                    $"-{consonant}{vowel}",
                    $"-{consonant}_{vowel}",
                    $"- {consonant}_{vowel}",
                };
                foreach (var variation in dynamicVariations) {
                    if (HasOto(variation, tone) || HasOto(ValidateAlias(variation), tone)) {
                        return variation;
                    }
                }
            }

            if (type.Contains("dynMid")) {
                string consonant = "";
                string vowel = "";
                if (alias.Contains(" ")) {
                    var parts = alias.Split(' ');
                    consonant = parts[0];
                    vowel = parts[1];
                } else {
                    consonant = alias;
                }
                var dynamicVariations1 = new List<string> {
                    $"{consonant}{vowel}",
                    $"{consonant} {vowel}",
                    $"{consonant}_{vowel}",
                };
                foreach (var variation1 in dynamicVariations1) {
                    if (HasOto(variation1, tone) || HasOto(ValidateAlias(variation1), tone)) {
                        return variation1;
                    }
                }
            }

            if (type.Contains("dynEnd")) {
                string consonant = "";
                string vowel = "";
                if (alias.Contains(" ")) {
                    var parts = alias.Split(' ');
                    consonant = parts[1];
                    vowel = parts[0];
                } else {
                    consonant = alias;
                }
                var dynamicVariations1 = new List<string> {
                    $"{vowel}{consonant} -",
                    $"{vowel} {consonant}-",
                    $"{vowel}{consonant}-",
                    $"{vowel} {consonant} -",
                };
                foreach (var variation1 in dynamicVariations1) {
                    if (HasOto(variation1, tone) || HasOto(ValidateAlias(variation1), tone)) {
                        return variation1;
                    }
                }
            }

            var formatsToTry = aliasFormats[type];
            int counter = 0;
            foreach (var format in formatsToTry) {
                string aliasFormat;
                if (type.Contains("mix") && counter < 4) {
                    aliasFormat = (counter % 2 == 0) ? $"{alias}{format}" : $"{format}{alias}";
                    counter++;
                } else if (type.Contains("end") || type.Contains("End") && !(type.Contains("dynEnd"))) {
                    aliasFormat = $"{alias}{format}";
                } else {
                    aliasFormat = $"{format}{alias}";
                }
                
                if (HasOto(aliasFormat, tone) || HasOto(ValidateAlias(aliasFormat), tone)) {
                    return aliasFormat;
                }
            }
            return alias;
        }

        protected override string ValidateAlias(string alias) {
            if (isMissingVPhonemes) {
                foreach (var fb in missingVphonemes.OrderByDescending(f => f.Key.Length)) {
                    alias = alias.Replace(fb.Key, fb.Value);
                }
            }
            if (isMissingCPhonemes) {
                foreach (var fb in missingCphonemes.OrderByDescending(f => f.Key.Length)) {
                    alias = alias.Replace(fb.Key, fb.Value);
                }
            }
            if (isTimitPhonemes) {
                foreach (var fb in timitphonemes.OrderByDescending(f => f.Key.Length)) {
                    alias = alias.Replace(fb.Key, fb.Value);
                }
            }
            return alias;
        }

        bool PhonemeIsPresent(string alias, string phoneme) {
            if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(phoneme)) return false;
            if (alias == phoneme) return true;
            return alias.EndsWith(phoneme);
        }
        
        protected override bool NoGap => true;

        protected override double GetTransitionMultiplier(string alias) {
            return 1.0; 
        }

        protected override double GetTransitionBasicLengthMs(string alias, int tone, PhonemeAttributes attr) {
            double otoLength = GetTransitionBasicLengthMsByOto(alias, tone, attr);

            var tokens = alias.Split(' ')
                      .Select(t => t.Replace("-", "").Trim())
                      .Where(t => !string.IsNullOrEmpty(t))
                      .ToList();

            var sortedOverrides = PhonemeOverrides.OrderByDescending(kv => kv.Key.Length);
            foreach (var kvp in sortedOverrides) {
                var symbol = kvp.Key;
                var value = kvp.Value;

                if (symbol.Contains(" ")) {
                    if (alias.Replace("-", "").Contains(symbol)) {
                        return GetTransitionBasicLengthMsByConstant() * value;
                    }
                } 
                else if (tokens.Contains(symbol)) {
                    return GetTransitionBasicLengthMsByConstant() * value;
                }
            }

            return otoLength;
        }
    }
}