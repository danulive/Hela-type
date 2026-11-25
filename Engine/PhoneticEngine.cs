using System;
using System.Collections.Generic;
using System.Text;

namespace HelaType.Engine;

public readonly record struct PhoneticCandidate(
    string SinhalaText,
    string RomanEquivalent,
    string CategoryHint);

public readonly record struct PhoneticRuleEntry(
    string Category,
    string SinglishKey,
    string SinhalaOutput,
    string ExampleInput,
    string ExampleOutput,
    string Notes);

public static class PhoneticEngine
{
    private enum TokenKind
    {
        IndependentVowel,
        DependentVowel,
        Consonant,
        SpecialMark,
        Yansaya,
        Rakaransaya,
        Rephaya,
        Raw
    }

    private readonly record struct ParsedToken(string Roman, TokenKind Kind);
    private readonly record struct VowelRule(string Roman, string Sinhala);
    private readonly record struct ConsonantRule(string Roman, string Sinhala, bool TakesHalKirima);

    private const char HalKirima = '\u0DCA';
    private const char Zwj = '\u200D';
    private const char Yayanna = '\u0DBA';
    private const char Rayanna = '\u0DBB';

    // Longest-match ordered independent vowels (word-initial or post-vowel/anusvara/visarga)
    private static readonly VowelRule[] IndependentVowels =
    [
        new("aee", "\u0D88"), // ඈ
        new("AA",  "\u0D88"), // ඈ
        new("aa",  "\u0D86"), // ආ
        new("a~",  "\u0D86"), // ආ
        new("ae",  "\u0D87"), // ඇ
        new("ai",  "\u0D93"), // ඓ
        new("au",  "\u0D96"), // ඖ
        new("ou",  "\u0D96"), // ඖ
        new("ii",  "\u0D8A"), // ඊ
        new("i~",  "\u0D8A"), // ඊ
        new("uu",  "\u0D8C"), // ඌ
        new("u~",  "\u0D8C"), // ඌ
        new("ee",  "\u0D92"), // ඒ
        new("e~",  "\u0D92"), // ඒ
        new("oo",  "\u0D95"), // ඕ
        new("o~",  "\u0D95"), // ඕ
        new("a",   "\u0D85"), // අ
        new("A",   "\u0D87"), // ඇ
        new("i",   "\u0D89"), // ඉ
        new("I",   "\u0D89"), // ඉ
        new("u",   "\u0D8B"), // උ
        new("U",   "\u0D8B"), // උ
        new("e",   "\u0D91"), // එ
        new("E",   "\u0D91"), // එ
        new("o",   "\u0D94"), // ඔ
        new("O",   "\u0D94"), // ඔ
    ];

    // Longest-match ordered dependent vowel signs (Pillam), replacing consonant Hal Kirima (\u0DCA)
    private static readonly VowelRule[] DependentVowels =
    [
        new("ruu", "\u0DF2"), // ෲ (Diga Gaetta-pilla)
        new("ru",  "\u0DD8"), // ෘ (Gaetta-pilla)
        new("aee", "\u0DD1"), // ෑ
        new("AA",  "\u0DD1"), // ෑ
        new("aa",  "\u0DCF"), // ා
        new("a~",  "\u0DCF"), // ා
        new("ae",  "\u0DD0"), // ැ
        new("ai",  "\u0DDB"), // ෛ
        new("au",  "\u0DDE"), // ෞ
        new("ou",  "\u0DDE"), // ෞ
        new("ii",  "\u0DD3"), // ී
        new("i~",  "\u0DD3"), // ී
        new("uu",  "\u0DD6"), // ූ
        new("u~",  "\u0DD6"), // ූ
        new("ee",  "\u0DDA"), // ේ
        new("e~",  "\u0DDA"), // ේ
        new("oo",  "\u0DDD"), // ෝ
        new("o~",  "\u0DDD"), // ෝ
        new("a",   ""),       // Inherent vowel (removes Hal Kirima)
        new("A",   "\u0DD0"), // ැ
        new("i",   "\u0DD2"), // ි
        new("I",   "\u0DD2"), // ි
        new("u",   "\u0DD4"), // ු
        new("U",   "\u0DD4"), // ු
        new("e",   "\u0DD9"), // ෙ
        new("E",   "\u0DD9"), // ෙ
        new("o",   "\u0DDC"), // ො
        new("O",   "\u0DDC"), // ො
    ];

    // Longest-match ordered consonants (4 -> 3 -> 2 -> 1 characters).
    // Note: Per Unicode Sinhala block, U+0DA4 is ඤ (ny) and U+0DA5 is ඥ (gny/jny).
    private static readonly ConsonantRule[] Consonants =
    [
        // 4-char clusters
        new("nndh", "\u0DB3", true),  // ඳ (Sanyaka Dayanna)

        // 3-char clusters
        new("nnd",  "\u0DAC", true),  // ඬ (Sanyaka Ddayanna)
        new("nng",  "\u0D9F", true),  // ඟ (Sanyaka Gayanna)
        new("mmb",  "\u0DB9", true),  // ඹ (Amba Bayanna)
        new("ksh",  "\u0D9A\u0DCA\u200D\u0DC2", true), // ක්ෂ
        new("gny",  "\u0DA5", true),  // ඥ
        new("jny",  "\u0DA5", true),  // ඥ
        new("chh",  "\u0DA1", true),  // ඡ
        new("thh",  "\u0DAE", true),  // ථ
        new("dhh",  "\u0DB0", true),  // ධ

        // 2-char clusters
        new("ny",   "\u0DA4", true),  // ඤ
        new("Ch",   "\u0DA1", true),  // ඡ
        new("ch",   "\u0DA0", true),  // ච
        new("th",   "\u0DAD", true),  // ත
        new("dh",   "\u0DAF", true),  // ද
        new("Th",   "\u0DA8", true),  // ඨ
        new("Dh",   "\u0DAA", true),  // ඪ
        new("kh",   "\u0D9B", true),  // ඛ
        new("gh",   "\u0D9D", true),  // ඝ
        new("jh",   "\u0DA3", true),  // ඣ
        new("ph",   "\u0DB5", true),  // ඵ
        new("bh",   "\u0DB7", true),  // භ
        new("sh",   "\u0DC1", true),  // ශ
        new("Sh",   "\u0DC2", true),  // ෂ
        new("ng",   "\u0D82", false), // ං (Anusvara - no Hal Kirima)
        new("N~",   "\u0D82", false), // ං (Anusvara - no Hal Kirima)

        // 1-char consonants
        new("c",    "\u0DA0", true),  // ච
        new("C",    "\u0DA0", true),  // ච
        new("t",    "\u0DA7", true),  // ට
        new("T",    "\u0DA7", true),  // ට
        new("d",    "\u0DA9", true),  // ඩ
        new("D",    "\u0DA9", true),  // ඩ
        new("K",    "\u0D9B", true),  // ඛ
        new("k",    "\u0D9A", true),  // ක
        new("G",    "\u0D9D", true),  // ඝ
        new("g",    "\u0D9C", true),  // ග
        new("J",    "\u0DA3", true),  // ඣ
        new("j",    "\u0DA2", true),  // ජ
        new("P",    "\u0DB5", true),  // ඵ
        new("p",    "\u0DB4", true),  // ප
        new("B",    "\u0DB7", true),  // භ
        new("b",    "\u0DB6", true),  // බ
        new("S",    "\u0DC2", true),  // ෂ
        new("s",    "\u0DC3", true),  // ස
        new("H",    "\u0D83", false), // ඃ (Visarga - no Hal Kirima)
        new("N",    "\u0DAB", true),  // ණ
        new("n",    "\u0DB1", true),  // න
        new("L",    "\u0DC5", true),  // ළ
        new("l",    "\u0DBD", true),  // ල
        new("m",    "\u0DB8", true),  // ම
        new("M",    "\u0DB8", true),  // ම
        new("y",    "\u0DBA", true),  // ය
        new("Y",    "\u0DBA", true),  // ය
        new("r",    "\u0DBB", true),  // ර
        new("v",    "\u0DC0", true),  // ව
        new("V",    "\u0DC0", true),  // ව
        new("w",    "\u0DC0", true),  // ව
        new("W",    "\u0DC0", true),  // ව
        new("h",    "\u0DC4", true),  // හ
        new("f",    "\u0DC6", true),  // ෆ
        new("F",    "\u0DC6", true),  // ෆ
    ];

    private static readonly Dictionary<string, string[]> PrimarySwaps = new(StringComparer.Ordinal)
    {
        ["t"]   = ["th"],
        ["th"]  = ["t"],
        ["d"]   = ["dh"],
        ["dh"]  = ["d"],
        ["n"]   = ["N"],
        ["N"]   = ["n"],
        ["l"]   = ["L"],
        ["L"]   = ["l"],
        ["s"]   = ["sh", "Sh"],
        ["sh"]  = ["s", "Sh"],
        ["Sh"]  = ["sh", "s"],
        ["ng"]  = ["nng", "ngg"],
        ["nng"] = ["ng", "ngg"],
        ["nnd"] = ["nd"],
        ["nndh"] = ["ndh"],
        ["mmb"] = ["mb"],
    };

    private static readonly Dictionary<string, string[]> SecondarySwaps = new(StringComparer.Ordinal)
    {
        ["th"]  = ["thh"],
        ["thh"] = ["th"],
        ["dh"]  = ["dhh"],
        ["dhh"] = ["dh"],
        ["t"]   = ["Th"],
        ["Th"]  = ["t"],
        ["d"]   = ["Dh"],
        ["Dh"]  = ["d"],
        ["k"]   = ["kh"],
        ["kh"]  = ["k"],
        ["g"]   = ["gh"],
        ["gh"]  = ["g"],
        ["p"]   = ["ph"],
        ["ph"]  = ["p"],
        ["b"]   = ["bh"],
        ["bh"]  = ["b"],
        ["ch"]  = ["chh"],
        ["chh"] = ["ch"],
        ["c"]   = ["chh"],
    };

    public static string Transliterate(string roman)
    {
        if (string.IsNullOrEmpty(roman))
        {
            return string.Empty;
        }

        return TransliterateInternal(roman, tokens: null);
    }

    public static IReadOnlyList<PhoneticCandidate> GetCandidates(string roman)
    {
        if (string.IsNullOrEmpty(roman))
        {
            return Array.Empty<PhoneticCandidate>();
        }

        var tokens = new List<ParsedToken>(roman.Length);
        string primary = TransliterateInternal(roman, tokens);

        var candidates = new List<PhoneticCandidate>(3)
        {
            new(primary, roman, "Primary")
        };
        var seen = new HashSet<string>(StringComparer.Ordinal) { primary };

        void TryAddCandidate(string candidateRoman, string hint)
        {
            if (candidates.Count >= 3)
            {
                return;
            }

            string sinhala = TransliterateInternal(candidateRoman, tokens: null);
            if (seen.Add(sinhala))
            {
                candidates.Add(new PhoneticCandidate(sinhala, candidateRoman, hint));
            }
        }

        string[] tokenStrings = new string[tokens.Count];
        for (int t = 0; t < tokens.Count; t++)
        {
            tokenStrings[t] = tokens[t].Roman;
        }

        // Pass 1: Nasal cluster ambiguities (nd -> ndh / nnd, ndh -> nndh, mb -> mmb) & ng -> nng/ngg
        for (int i = tokens.Count - 1; i >= 0 && candidates.Count < 3; i--)
        {
            string tok = tokenStrings[i];

            if (i + 1 < tokens.Count)
            {
                string next = tokenStrings[i + 1];
                if (tok == "n" && next == "d")
                {
                    TryAddCandidate(ReplaceTwo(tokenStrings, i, "n", "dh"), "Dental ද");
                    TryAddCandidate(ReplaceTwo(tokenStrings, i, "nnd"), "Sanyaka ඬ");
                }
                else if (tok == "n" && next == "dh")
                {
                    TryAddCandidate(ReplaceTwo(tokenStrings, i, "nndh"), "Sanyaka ඳ");
                }
                else if (tok == "m" && next == "b")
                {
                    TryAddCandidate(ReplaceTwo(tokenStrings, i, "mmb"), "Sanyaka ඹ");
                }
            }

            if (tok is "ng" or "nng" && PrimarySwaps.TryGetValue(tok, out string[]? ngAlts))
            {
                foreach (string alt in ngAlts)
                {
                    TryAddCandidate(ReplaceOne(tokenStrings, i, alt), alt == "nng" ? "Sanyaka ඟ" : "Anusvara / ංග");
                }
            }
        }

        // Pass 2: Primary dental / retroflex / lateral / sibilant ambiguities (rightmost first, skipping split geminates like nn)
        for (int i = tokens.Count - 1; i >= 0 && candidates.Count < 3; i--)
        {
            string tok = tokenStrings[i];
            if (tok is "ng" or "nng")
            {
                continue;
            }

            if (IsPartOfGeminate(tokenStrings, i))
            {
                continue;
            }

            if (PrimarySwaps.TryGetValue(tok, out string[]? alts))
            {
                foreach (string alt in alts)
                {
                    TryAddCandidate(ReplaceOne(tokenStrings, i, alt), DescribeSwapHint(alt));
                }
            }
        }

        // Pass 3: Secondary aspirate / retroflex-aspirate ambiguities and unskipped tokens
        for (int i = tokens.Count - 1; i >= 0 && candidates.Count < 3; i--)
        {
            string tok = tokenStrings[i];

            if (IsPartOfGeminate(tokenStrings, i) && PrimarySwaps.TryGetValue(tok, out string[]? gemAlts))
            {
                foreach (string alt in gemAlts)
                {
                    TryAddCandidate(ReplaceOne(tokenStrings, i, alt), DescribeSwapHint(alt));
                }
            }

            if (SecondarySwaps.TryGetValue(tok, out string[]? secAlts))
            {
                foreach (string alt in secAlts)
                {
                    TryAddCandidate(ReplaceOne(tokenStrings, i, alt), DescribeSwapHint(alt));
                }
            }
        }

        return candidates;
    }

    private static string TransliterateInternal(string roman, List<ParsedToken>? tokens)
    {
        var sb = new StringBuilder(roman.Length * 2);
        bool hasPendingConsonant = false;
        bool canAttachMedialConjunct = false;
        char lastConsonantBase = '\0';

        int i = 0;
        int len = roman.Length;

        while (i < len)
        {
            if (hasPendingConsonant)
            {
                if (TryMatchVowel(roman, i, DependentVowels, out VowelRule depVowel))
                {
                    if (sb.Length > 0 && sb[^1] == HalKirima)
                    {
                        sb.Length--;
                    }
                    if (depVowel.Sinhala.Length > 0)
                    {
                        sb.Append(depVowel.Sinhala);
                    }

                    tokens?.Add(new ParsedToken(depVowel.Roman, TokenKind.DependentVowel));
                    hasPendingConsonant = false;
                    canAttachMedialConjunct = false;
                    lastConsonantBase = '\0';
                    i += depVowel.Roman.Length;
                    continue;
                }

                if (canAttachMedialConjunct)
                {
                    char current = roman[i];
                    if (current == 'y' && lastConsonantBase != Yayanna && lastConsonantBase != Rayanna)
                    {
                        if (i + 1 == len || IsRegularVowelStart(roman[i + 1]))
                        {
                            sb.Append(Zwj).Append(Yayanna).Append(HalKirima);
                            tokens?.Add(new ParsedToken("y", TokenKind.Yansaya));
                            hasPendingConsonant = true;
                            canAttachMedialConjunct = false;
                            lastConsonantBase = Yayanna;
                            i++;
                            continue;
                        }
                    }

                    if (current == 'r' && lastConsonantBase != Rayanna && lastConsonantBase != Yayanna)
                    {
                        if (i + 1 == len || IsRegularVowelStart(roman[i + 1]))
                        {
                            sb.Append(Zwj).Append(Rayanna).Append(HalKirima);
                            tokens?.Add(new ParsedToken("r", TokenKind.Rakaransaya));
                            hasPendingConsonant = true;
                            canAttachMedialConjunct = false;
                            lastConsonantBase = Rayanna;
                            i++;
                            continue;
                        }
                    }
                }
            }

            if (!hasPendingConsonant)
            {
                if (roman[i] == 'R')
                {
                    if (i + 1 < len && !IsRegularVowelStart(roman[i + 1]) &&
                        (TryMatchConsonant(roman, i + 1, out _) || roman[i + 1] == 'R'))
                    {
                        sb.Append(Rayanna).Append(HalKirima).Append(Zwj);
                        tokens?.Add(new ParsedToken("R", TokenKind.Rephaya));
                        hasPendingConsonant = false;
                        canAttachMedialConjunct = false;
                        lastConsonantBase = '\0';
                        i++;
                        continue;
                    }

                    if (i + 1 == len)
                    {
                        sb.Append(Rayanna).Append(HalKirima);
                        tokens?.Add(new ParsedToken("R", TokenKind.Rephaya));
                        hasPendingConsonant = false;
                        canAttachMedialConjunct = false;
                        lastConsonantBase = '\0';
                        i++;
                        continue;
                    }

                    // Followed immediately by a vowel -> act as Rayanna consonant
                    sb.Append(Rayanna).Append(HalKirima);
                    tokens?.Add(new ParsedToken("R", TokenKind.Consonant));
                    hasPendingConsonant = true;
                    canAttachMedialConjunct = false;
                    lastConsonantBase = Rayanna;
                    i++;
                    continue;
                }

                if (TryMatchVowel(roman, i, IndependentVowels, out VowelRule indepVowel))
                {
                    sb.Append(indepVowel.Sinhala);
                    tokens?.Add(new ParsedToken(indepVowel.Roman, TokenKind.IndependentVowel));
                    hasPendingConsonant = false;
                    canAttachMedialConjunct = false;
                    lastConsonantBase = '\0';
                    i += indepVowel.Roman.Length;
                    continue;
                }
            }

            if (roman[i] == 'R')
            {
                sb.Append(Rayanna).Append(HalKirima);
                tokens?.Add(new ParsedToken("R", TokenKind.Consonant));
                hasPendingConsonant = true;
                canAttachMedialConjunct = true;
                lastConsonantBase = Rayanna;
                i++;
                continue;
            }

            if (TryMatchConsonant(roman, i, out ConsonantRule consonant))
            {
                sb.Append(consonant.Sinhala);
                if (consonant.TakesHalKirima)
                {
                    sb.Append(HalKirima);
                    tokens?.Add(new ParsedToken(consonant.Roman, TokenKind.Consonant));
                    hasPendingConsonant = true;
                    canAttachMedialConjunct = true;
                    lastConsonantBase = consonant.Sinhala[^1];
                }
                else
                {
                    tokens?.Add(new ParsedToken(consonant.Roman, TokenKind.SpecialMark));
                    hasPendingConsonant = false;
                    canAttachMedialConjunct = false;
                    lastConsonantBase = '\0';
                }

                i += consonant.Roman.Length;
                continue;
            }

            sb.Append(roman[i]);
            tokens?.Add(new ParsedToken(roman[i].ToString(), TokenKind.Raw));
            hasPendingConsonant = false;
            canAttachMedialConjunct = false;
            lastConsonantBase = '\0';
            i++;
        }

        return sb.ToString();
    }

    private static bool TryMatchVowel(string input, int index, VowelRule[] rules, out VowelRule matched)
    {
        foreach (VowelRule rule in rules)
        {
            if (StartsWithOrdinal(input, index, rule.Roman))
            {
                matched = rule;
                return true;
            }
        }

        matched = default;
        return false;
    }

    private static bool TryMatchConsonant(string input, int index, out ConsonantRule matched)
    {
        foreach (ConsonantRule rule in Consonants)
        {
            if (StartsWithOrdinal(input, index, rule.Roman))
            {
                matched = rule;
                return true;
            }
        }

        matched = default;
        return false;
    }

    private static bool StartsWithOrdinal(string source, int index, string prefix)
    {
        if (index + prefix.Length > source.Length)
        {
            return false;
        }

        for (int j = 0; j < prefix.Length; j++)
        {
            if (source[index + j] != prefix[j])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRegularVowelStart(char c) =>
        c is 'a' or 'A' or 'i' or 'I' or 'u' or 'U' or 'e' or 'E' or 'o' or 'O';

    private static bool IsPartOfGeminate(string[] tokens, int index)
    {
        string current = tokens[index];
        if (index > 0 && tokens[index - 1] == current)
        {
            return true;
        }

        if (index + 1 < tokens.Length && tokens[index + 1] == current)
        {
            return true;
        }

        return false;
    }

    private static string ReplaceOne(string[] tokens, int index, string replacement)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Length; i++)
        {
            sb.Append(i == index ? replacement : tokens[i]);
        }
        return sb.ToString();
    }

    private static string ReplaceTwo(string[] tokens, int index, string rep1, string? rep2 = null)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Length; i++)
        {
            if (i == index)
            {
                sb.Append(rep1);
                if (rep2 is not null)
                {
                    sb.Append(rep2);
                }
                i++; // skip tokens[index + 1]
            }
            else
            {
                sb.Append(tokens[i]);
            }
        }
        return sb.ToString();
    }

    private static string DescribeSwapHint(string targetToken) => targetToken switch
    {
        "th" => "Dental ත",
        "t" => "Retroflex ට",
        "dh" => "Dental ද",
        "d" => "Retroflex ඩ",
        "N" => "Retroflex ණ",
        "n" => "Dental න",
        "L" => "Retroflex ළ",
        "l" => "Dental ල",
        "sh" => "Palatal ශ",
        "Sh" => "Retroflex ෂ",
        "s" => "Dental ස",
        "Th" => "Aspirate ඨ",
        "Dh" => "Aspirate ඪ",
        "thh" => "Aspirate ථ",
        "dhh" => "Aspirate ධ",
        "kh" => "Aspirate ඛ",
        "gh" => "Aspirate ඝ",
        "ph" => "Aspirate ඵ",
        "bh" => "Aspirate භ",
        "chh" => "Aspirate ඡ",
        _ => "Variant"
    };

    public static IReadOnlyList<PhoneticRuleEntry> GetCheatSheetEntries() =>
    [
        // Vowels
        new("Vowels", "a / aa / a~", "අ / ආ", "ammaa", "අම්මා", "Short & long 'a'"),
        new("Vowels", "ae / A / aee / AA", "ඇ / ඈ", "aetha / Aetha", "ඇත", "Short & long 'ae'"),
        new("Vowels", "i / ii / i~", "ඉ / ඊ", "ira / iithala", "ඉර / ඊතල", "Short & long 'i'"),
        new("Vowels", "u / uu / u~", "උ / ඌ", "uda / uuraa", "උඩ / ඌරා", "Short & long 'u'"),
        new("Vowels", "e / ee / e~", "එ / ඒ", "eka / eekaa", "එක / ඒකා", "Short & long 'e'"),
        new("Vowels", "ai", "ඓ / ෛ", "maithrii", "මෛත්‍රී", "Kombu deka / Ai"),
        new("Vowels", "o / oo / o~", "ඔ / ඕ", "oya / oona", "ඔය / ඕන", "Short & long 'o'"),
        new("Vowels", "au / ou", "ඖ / ෞ", "gauthama", "ගෞතම", "Gayanukitta / Au"),
        new("Vowels", "C + ru / ruu", "ෘ / ෲ", "kruthya / kruu", "කෘත්‍ය / කෲ", "Gaetta-pilla after consonant"),

        // Basic & Aspirated Consonants
        new("Consonants", "k / kh (K)", "ක / ඛ", "katha / khadga", "කත / ඛඩ්ග", "Velar stops"),
        new("Consonants", "g / gh (G)", "ග / ඝ", "gama / ghoosha", "ගම / ඝෝෂ", "Voiced velar"),
        new("Consonants", "ch (c) / chh (Ch)", "ච / ඡ", "chithra / chhaayaa", "චිත්‍ර / ඡායා", "Palatal stops"),
        new("Consonants", "j / jh (J)", "ජ / ඣ", "jaya / jhasha", "ජය / ඣෂ", "Voiced palatal"),
        new("Consonants", "t / Th", "ට / ඨ", "rata / paatha", "රට / පාඨ", "Retroflex stops"),
        new("Consonants", "d / Dh", "ඩ / ඪ", "uda / Dhaa", "උඩ / ඪා", "Voiced retroflex"),
        new("Consonants", "th / thh", "ත / ථ", "tharu / sthaana", "තරු / ස්ථාන", "Dental stops"),
        new("Consonants", "dh / dhh", "ද / ධ", "dhena / dhharma", "දෙන / ධර්ම", "Voiced dental"),
        new("Consonants", "n / N", "න / ණ", "nama / tharuNa", "නම / තරුණ", "Dental vs Retroflex N"),
        new("Consonants", "p / ph (P)", "ප / ඵ", "paara / phala", "පාර / ඵල", "Labial stops"),
        new("Consonants", "b / bh (B)", "බ / භ", "bala / bhaashaa", "බල / භාෂා", "Voiced labial"),
        new("Consonants", "m / y / r", "ම / ය / ර", "mayura", "මයුර", "Labial & approximants"),
        new("Consonants", "l / L", "ල / ළ", "heLakuru", "හෙළකුරු", "Dental vs Retroflex L"),
        new("Consonants", "v / w / h / f", "ව / හ / ෆ", "wathura / foto", "වතුර / ෆොටො", "Labiodental & glottal"),
        new("Consonants", "s / sh / Sh (S)", "ස / ශ / ෂ", "sinhala / shrii / bhaaShaa", "සින්හල / ශ්‍රී / භාෂා", "Sibilants"),

        // Sanyaka & Nasal Marks
        new("Sanyaka & Special", "nng", "ඟ", "gannga", "ගඟ", "Sanyaka Gayanna"),
        new("Sanyaka & Special", "nnd", "ඬ", "hannda", "හඬ", "Sanyaka Ddayanna"),
        new("Sanyaka & Special", "nndh", "ඳ", "hanndha", "හඳ", "Sanyaka Dayanna"),
        new("Sanyaka & Special", "mmb", "ඹ", "ammbaa", "අඹා", "Amba Bayanna"),
        new("Sanyaka & Special", "ng / N~", "ං", "singhala / langkaa", "සිංහල / ලංකා", "Anusvara (Binduwa)"),
        new("Sanyaka & Special", "H", "ඃ", "dhuHkhaya", "දුඃඛය", "Visarga"),

        // Conjuncts & Clusters
        new("Conjuncts", "ny / gny / jny", "ඤ / ඥ", "nyaaya / pragnyaa", "ඤාය / ප්‍රඥා", "Palatal nasal & cluster"),
        new("Conjuncts", "ksh", "ක්‍ෂ", "lakshya", "ලක්‍ෂ්‍ය", "Kayanna + ZWJ + Murdhaja S"),
        new("Conjuncts", "C + y + V", "්‍ය (Yansaya)", "kya / vidhyaa", "ක්‍ය / විද්‍යා", "Yansaya medial glide"),
        new("Conjuncts", "C + r + V", "්‍ර (Rakaransaya)", "kra / pri / shrii", "ක්‍ර / ප්‍රි / ශ්‍රී", "Rakaransaya medial glide"),
        new("Conjuncts", "V + R + C", "ර් + ZWJ (Rephaya)", "kaRma / suuRyaa", "කර්ම / සූර්යා", "Rephaya above consonant"),
    ];
}
