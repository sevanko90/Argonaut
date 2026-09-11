#!/usr/bin/env python3
"""Generates mixed raw test data for the Argonaut raw viewer.

Layout (tags are grep-able, all wrapped in ### ... ###):
  - line 1..2: comma-free prose header (so FileTypeDetector yields Unidentified)
  - ###TAG_START### near the top
  - the Unicode catalogue: one tagged line per editor-tripping case (see UNICODE_CASES)
  - normal lines 60-200 chars, with ###TAG_CHECKPOINT_NNN### every ~100MB
  - at ~1GB:   one ~100MB line: ###TAG_BIGLINE_A_START### ... ###TAG_BIGLINE_A_MID### ... ###TAG_BIGLINE_A_END###
  - at ~2GB:   ###TAG_BINARY### then ~10MB of random binary bytes (invalid UTF-8, control chars)
  - at ~3GB:   the second ~100MB line (BIGLINE_B, same start/mid/end tags)
  - the Unicode catalogue again, so the cases exist deep into a huge file too
  - last line: ###TAG_END###

Usage:
  make-raw-sample.py                      # the full ~4GB file
  make-raw-sample.py --size 50M           # a smaller one, same shape
  make-raw-sample.py --unicode-only       # just the catalogue, a few KB, opens instantly
  make-raw-sample.py --out ~/some/file.txt --size 2G
"""
import argparse, os, random, sys, time

DEFAULT_OUT = os.path.expanduser("~/testData/raw-sample-4gb.dat")
UNICODE_ONLY_OUT = os.path.expanduser("~/testData/raw-unicode-sample.txt")
DEFAULT_TARGET = 4 * 1024**3
CHECKPOINT_EVERY = 100 * 1024**2
BIGLINE_SIZE = 100 * 1024**2
BINARY_SIZE = 10 * 1024**2

random.seed(42)
WORDS = ("alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima "
         "mike november oscar papa quebec romeo sierra tango uniform victor whiskey "
         "xray yankee zulu lorem ipsum dolor sit amet consectetur adipiscing elit "
         "payload sensor telemetry packet frame buffer offset segment index marker").split()

# Sprinkled through the prose so multi-byte characters, combining marks and astral
# planes turn up everywhere in the file, not only in the catalogue - which is what
# exercises the wrap-width backoff and caret movement at scale.
UNICODE_WORDS = "日本語 テスト données café naïve Straße αβγ Москва 🌍 👨‍👩‍👧‍👦 ﬁcher ＡＢＣ ก้ ế".split()
UNICODE_WORD_CHANCE = 0.05


def _b(*parts):
    """Concatenates str (as UTF-8) and bytes, so a line can mix text and raw invalid bytes."""
    return b"".join(p.encode() if isinstance(p, str) else p for p in parts)


# Invisible characters are written as named code points rather than pasted in: a test file for
# whitespace and zero-width gotchas is unreadable - and un-reviewable - if the gotchas are
# invisible in its own source too.
NBSP = chr(0x00A0)          # no-break space
SHY = chr(0x00AD)           # soft hyphen
NEL = chr(0x0085)           # C1 next line
OGHAM_SPACE = chr(0x1680)
EN_SPACE = chr(0x2002)
EM_SPACE = chr(0x2003)
THIN_SPACE = chr(0x2009)
HAIR_SPACE = chr(0x200A)
FIGURE_SPACE = chr(0x2007)
PUNCT_SPACE = chr(0x2008)
NARROW_NBSP = chr(0x202F)
IDEOGRAPHIC_SPACE = chr(0x3000)
ZWSP = chr(0x200B)
ZWNJ = chr(0x200C)
ZWJ = chr(0x200D)
WORD_JOINER = chr(0x2060)
BOM = chr(0xFEFF)
LINE_SEP = chr(0x2028)
PARA_SEP = chr(0x2029)
RLO = chr(0x202E)           # right-to-left override
PDF = chr(0x202C)           # pop directional formatting
LRI = chr(0x2066)           # left-to-right isolate
RLI = chr(0x2067)
PDI = chr(0x2069)           # pop directional isolate
VS15 = chr(0xFE0E)          # text presentation
VS16 = chr(0xFE0F)          # emoji presentation
KEYCAP = chr(0x20E3)
ACUTE = chr(0x0301)         # combining marks
CIRCUMFLEX = chr(0x0302)
TILDE = chr(0x0303)
MACRON = chr(0x0304)
DIAERESIS = chr(0x0308)
CEDILLA = chr(0x0327)
OGONEK = chr(0x0328)


# (tag, line) pairs. The tag is grep-able and the line holds the case itself, so every case can
# be found by offset and then clicked, caret-walked, selected and copied on its own.
UNICODE_CASES = [
    # --- multi-byte, no ASCII to fall back on -----------------------------------------
    ("KANJI", _b("日本語のテキストです。漢字とひらがなとカタカナ。")),
    ("CJK_NO_SPACES", _b("東京都渋谷区神南一丁目十九番十一号渋谷放送センター内郵便局留")),
    ("CJK_EXT_B", _b("𠜎 𠜱 𠝹 𠱓 𠱸 - astral CJK, four bytes and a surrogate pair each")),
    ("HANGUL", _b("한국어 텍스트 조합형 글자")),
    ("THAI_NO_SPACES", _b("ภาษาไทยเขียนติดกันไม่มีช่องว่างระหว่างคำ")),
    ("DEVANAGARI", _b("नमस्ते दुनिया - combining vowel signs and a virama cluster")),
    ("GREEK_CYRILLIC", _b("αβγδε ΑΒΓΔΕ Москва Привет - two bytes per letter")),
    # --- accents: same glyph, different bytes -----------------------------------------
    ("ACCENT_PRECOMPOSED", _b("café résumé naïve Ünicode éèêë")),
    ("ACCENT_DECOMPOSED", _b("cafe", ACUTE, " resume", ACUTE, " nai", DIAERESIS, "ve U", DIAERESIS,
                             "nicode - NFD: letter plus combining mark")),
    ("COMBINING_STACK", _b("a", ACUTE, CIRCUMFLEX, TILDE, MACRON, DIAERESIS,
                           " z", CEDILLA, OGONEK, " - one base, many marks")),
    ("TURKISH_DOTLESS", _b("Istanbul İstanbul ı İ - dotted and dotless i")),
    ("LIGATURE_FULLWIDTH", _b("ﬁle ﬂag ＡＢＣ１２３ - ligatures and fullwidth forms")),
    # --- whitespace that is not a space -----------------------------------------------
    ("SPACE_NBSP", _b("no", NBSP, "break", NBSP, "space", NBSP, "between", NBSP, "these", NBSP, "words")),
    ("SPACE_NARROW_NBSP", _b("narrow", NARROW_NBSP, "nbsp figure", FIGURE_SPACE, "space punctuation",
                             PUNCT_SPACE, "space")),
    ("SPACE_IDEOGRAPHIC", _b("ideographic", IDEOGRAPHIC_SPACE, "space", IDEOGRAPHIC_SPACE, "between",
                             IDEOGRAPHIC_SPACE, "words")),
    ("SPACE_EN_EM", _b("en", EN_SPACE, "space em", EM_SPACE, "space thin", THIN_SPACE, "space hair",
                       HAIR_SPACE, "space")),
    ("SPACE_OGHAM", _b("ogham", OGHAM_SPACE, "space - whitespace that draws as a line")),
    ("TAB_AND_VT", _b("tab\there\tand\tvertical\vtab\vand\fform\ffeed")),
    ("WHITESPACE_ONLY", _b("  ", NBSP, IDEOGRAPHIC_SPACE, "\t", EM_SPACE, ZWSP, " ")),
    # --- zero width: present in the bytes, absent on screen ----------------------------
    ("ZERO_WIDTH", _b("zero", ZWSP, "width", ZWSP, "space and word", WORD_JOINER, "joiner here")),
    ("ZWNJ_ZWJ", _b("zwnj", ZWNJ, "here and zwj", ZWJ, "here")),
    ("BOM_MIDLINE", _b("byte order mark ", BOM, " in the middle of a line")),
    ("SOFT_HYPHEN", _b("soft", SHY, "hyphen", SHY, "splits", SHY, "only", SHY, "when", SHY, "wrapped")),
    # --- direction --------------------------------------------------------------------
    ("RTL_HEBREW", _b("shalom שלום עולם mixed with latin")),
    ("RTL_ARABIC", _b("arabic مرحبا بالعالم mixed with latin")),
    ("BIDI_OVERRIDE", _b("override ", RLO, "reversed text", PDF, " back to normal")),
    ("BIDI_ISOLATE", _b("isolate ", LRI, "first", PDI, " ", RLI, "second", PDI, " done")),
    # --- emoji and grapheme clusters ---------------------------------------------------
    ("EMOJI_BASIC", _b("basic 😀 🌍 🚀 - one astral code point each")),
    ("EMOJI_ZWJ_FAMILY", _b("family 👨", ZWJ, "👩", ZWJ, "👧", ZWJ, "👦 and 🧑", ZWJ, "💻 - ZWJ sequences")),
    ("EMOJI_SKIN_TONE", _b("wave 👋🏽 thumbs 👍🏿 - base plus modifier")),
    ("EMOJI_FLAG", _b("flags 🇬🇧 🇯🇵 🇺🇳 - regional indicator pairs")),
    ("EMOJI_KEYCAP", _b("keycaps 1", VS16, KEYCAP, " 2", VS16, KEYCAP, " - base, VS16, combining keycap")),
    ("VARIATION_SELECTOR", _b("text ❤", VS15, " versus emoji ❤", VS16, " presentation")),
    ("MATH_ALPHANUMERIC", _b("𝔘𝔫𝔦𝔠𝔬𝔡𝔢 𝟙𝟚𝟛 - every letter a surrogate pair")),
    # --- characters that collide with the viewer's own display substitutions ------------
    ("LITERAL_REPLACEMENT", _b("a real U+FFFD ", chr(0xFFFD), " in valid UTF-8, not a decode failure")),
    ("LITERAL_CONTROL_PICTURE", _b("a real control picture ", chr(0x2400), " ", chr(0x2421),
                                   " in valid UTF-8")),
    # --- invalid UTF-8: the bytes a decoder has to guess at -----------------------------
    ("INVALID_LONE_CONTINUATION", _b("lone continuation ", b"\x80\x81", " here")),
    ("INVALID_TRUNCATED_2", _b("truncated two byte ", b"\xc3", " here")),
    ("INVALID_TRUNCATED_3", _b("truncated three byte ", b"\xe2\x82", " here")),
    ("INVALID_TRUNCATED_4", _b("truncated four byte ", b"\xf0\x9f\x98", " here")),
    ("INVALID_OVERLONG", _b("overlong slash ", b"\xc0\xaf", " and overlong nul ", b"\xc0\x80", " here")),
    ("INVALID_SURROGATE_CESU8", _b("encoded surrogate ", b"\xed\xa0\x80", " here")),
    ("INVALID_OUT_OF_RANGE", _b("beyond U+10FFFF ", b"\xf5\x80\x80\x80", " and ", b"\xfe\xff", " here")),
    ("INVALID_FIVE_BYTE", _b("five byte sequence ", b"\xf8\x88\x80\x80\x80", " here")),
    ("INVALID_MIXED_RUN", _b("run of them ", b"\xff\xfe\xfd\xfc\x80\x80", " here")),
    ("INVALID_SPLIT_KANJI", _b("kanji with its last byte gone ", "日本語".encode()[:-1], " here")),
    # --- controls in the middle of text -------------------------------------------------
    ("CONTROL_C0", _b("bell ", b"\x07", " escape ", b"\x1b", " sub ", b"\x1a", " here")),
    ("CONTROL_NUL", _b("nul ", b"\x00", " inside an otherwise textual line")),
    ("CONTROL_DEL", _b("delete ", b"\x7f", " inside an otherwise textual line")),
    ("CONTROL_NEL_LS_PS", _b("nel ", NEL, " line sep ", LINE_SEP, " para sep ", PARA_SEP,
                             " - line breaks to some tools, not to others")),
    # --- line endings ---------------------------------------------------------------------
    ("EOL_CRLF", b"this line ends with CRLF\r"),
    ("EOL_CR_ONLY", b"old mac CR ending follows\rand this text came after the lone CR"),
    ("EOL_LONE_CR_MIDLINE", b"a lone \r carriage return with text either side"),
    # --- long runs: wrap-width backoff, and the word-selection cap -------------------------
    ("LONG_CJK", _b("日本" * 400)),
    ("LONG_COMBINING", _b(("e" + ACUTE) * 600)),
    ("LONGWORD_UNDER_CAP", b"u" + b"n" * 60_000),
    ("LONGWORD_OVER_CAP", b"o" + b"v" * 70_000),
    ("LONGWORD_CJK_OVER_CAP", _b("語" * 30_000)),
]


def unicode_catalogue():
    """The whole catalogue as one blob, each case on its own tagged line."""
    lines = [b"###UNICODE_CATALOGUE_START###"]
    for tag, line in UNICODE_CASES:
        lines.append(_b(f"###U_{tag}### ", line))
    lines.append(b"###UNICODE_CATALOGUE_END###")
    return b"\n".join(lines) + b"\n"


def normal_batch(first_line_no, n_lines):
    """n_lines pseudo-random prose lines, 60-200 chars each, numbered."""
    lines = []
    for i in range(n_lines):
        target_len = random.randint(60, 200)
        parts = [f"L{first_line_no + i:09d}"]
        length = len(parts[0])
        while length < target_len:
            w = random.choice(UNICODE_WORDS) if random.random() < UNICODE_WORD_CHANCE else random.choice(WORDS)
            parts.append(w)
            length += len(w) + 1
        lines.append(" ".join(parts))
    return ("\n".join(lines) + "\n").encode()


def bigline(name):
    """One ~100MB line with tags at start, middle and end. No newlines inside."""
    filler = ("".join(random.choice("abcdefghijklmnopqrstuvwxyz0123456789+/=") for _ in range(1024)) * 1024).encode()  # 1MB
    half = BIGLINE_SIZE // (2 * len(filler))
    return b"".join([f"###TAG_BIGLINE_{name}_START###".encode(),
                     filler * half,
                     f"###TAG_BIGLINE_{name}_MID###".encode(),
                     filler * half,
                     f"###TAG_BIGLINE_{name}_END###".encode(), b"\n"])


def parse_size(text):
    """'4G', '50M', '1024K' or a plain byte count."""
    units = {"K": 1024, "M": 1024**2, "G": 1024**3}
    suffix = text[-1].upper()
    return int(text[:-1]) * units[suffix] if suffix in units else int(text)


def write_sample(out, target):
    start = time.time()
    written = 0
    line_no = 1
    checkpoint = 1
    next_checkpoint = CHECKPOINT_EVERY
    # Landmarks only make sense in a file big enough to hold them; a small --size gets the
    # header, the catalogue and prose, which is the shape that matters at that size.
    biglines_at = {at: name for at, name in {1 * 1024**3: "A", 3 * 1024**3: "B"}.items() if at < target}
    binary_at = 2 * 1024**3
    binary_done = binary_at >= target

    with open(out, "wb", buffering=1024*1024) as f:
        def put(data):
            nonlocal written
            f.write(data)
            written += len(data)

        put(b"RAW SAMPLE FILE for the Argonaut raw viewer - unstructured prose header\n")
        put(b"second header line without structure so detection stays unidentified\n")
        put(b"###TAG_START###\n")
        put(unicode_catalogue())

        while written < target - 64:
            # landmarks due?
            for at, name in list(biglines_at.items()):
                if written >= at:
                    put(bigline(name))
                    del biglines_at[at]
            if not binary_done and written >= binary_at:
                put(b"###TAG_BINARY###\n")
                put(random.randbytes(BINARY_SIZE))
                put(b"\n")
                binary_done = True
            if written >= next_checkpoint:
                put(f"###TAG_CHECKPOINT_{checkpoint:03d}###\n".encode())
                checkpoint += 1
                next_checkpoint += CHECKPOINT_EVERY

            # Smaller batches near the end, so a small --size does not overshoot by a whole
            # 50k-line block. ~130 bytes is the mean prose line.
            n_lines = max(1_000, min(50_000, int((target - written) // 130)))
            batch = normal_batch(line_no, n_lines)
            line_no += n_lines
            put(batch)
            if line_no % 1_000_000 < 50_000:
                print(f"{written / 1024**3:.2f} GiB written ({time.time() - start:.0f}s)", flush=True)

        put(unicode_catalogue())
        put(b"###TAG_END###\n")

    print(f"DONE: {written:,} bytes in {time.time() - start:.0f}s -> {out}", flush=True)


def write_unicode_only(out):
    with open(out, "wb") as f:
        f.write(b"UNICODE SAMPLE for the Argonaut raw viewer - one tagged line per case\n")
        f.write(unicode_catalogue())

    print(f"DONE: {os.path.getsize(out):,} bytes, {len(UNICODE_CASES)} cases -> {out}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", help="output path")
    parser.add_argument("--size", default=str(DEFAULT_TARGET), help="target size, e.g. 4G, 50M (default 4G)")
    parser.add_argument("--unicode-only", action="store_true",
                        help="write only the Unicode catalogue - a few hundred KB, no prose, no landmarks")
    args = parser.parse_args()

    out = args.out or (UNICODE_ONLY_OUT if args.unicode_only else DEFAULT_OUT)
    out = os.path.expanduser(out)
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)

    if args.unicode_only:
        write_unicode_only(out)
    else:
        write_sample(out, parse_size(args.size))


if __name__ == "__main__":
    main()
