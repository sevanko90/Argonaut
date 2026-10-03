#!/usr/bin/env python3
"""Generates test JSON fixtures for the Argonaut JSON viewer:

  - corrupt-sample-4gb.json: ~4GB, valid up to ~3GB in, then structurally invalid - for
    testing JsonStructureIndex's partial-index / IndexFailure path (a scan that stops with
    items already published).
  - valid-sample-50mb.json: ~50MB, top-level array of 15 well-formed objects, each with
    string/number/bool/array-of-string/array-of-number/array-of-object properties, an "i18n"
    object of non-ASCII keys and values (scripts, emoji, combining marks, invisible characters,
    U+2028 and JSON unicode escapes), a
    JS-epoch-ms "date" property, and a 4-object-deep nested hierarchy - for baseline
    valid-file rendering. Object[1] (the second object) carries a single ~10MB string
    property to exercise overflow rendering for a pathologically long value.
  - geojson-sample-25mb.json: ~25MB GeoJSON FeatureCollection (RFC 7946) - the companion
    document for the annotated example schema Argonaut writes into the user schema folder
    (geojson-annotated.example.json). Copy that schema to a name without the .example.json
    suffix, bind it from the toolbar, and every construct it teaches has something here to
    label: a fixed-layout "bbox" tuple, a long "features" array of identical objects,
    coordinate arrays running to tens of thousands of positions, nested GeometryCollections
    for the recursive $ref, and free-form feature properties (one of them a JS-epoch-ms
    timestamp, so a date hint and a schema label land on the same row).
  - wide-object-sample.json: ~130MB, a root object of 3,000,000 keys whose last member holds
    another object of 1,000,000 keys - for timing JSONPath navigation into objects with
    millions of keys, which finds a member by reading the container's names in order. The
    paths worth typing into the toolbar's path box are printed when it is written.
  - config-sample.ini: ~2MB INI file for the raw view's config colouring - sections, every
    comment style, `=` and `: ` separators, quoted values, numbers, keywords, inline comments,
    and values that hold a colon or a `#` without either meaning anything (URLs, Windows paths,
    times). Every 50th section carries one ~20KB value, a single line long enough to wrap past
    the colouring lookback.

  - huge-sample-<N>gb.ndjson: 100GB of NDJSON by default (size set by --huge-gb), far past any laptop's
    RAM, for stress-testing indexing, scrolling, search and memory use on a file that cannot be
    held in memory. Two needle lines - at 50% and near the end - give search and go-to something
    unique to find; their byte offsets are printed. Only ever written when --huge is passed, never
    by a bare run, and refused when the disk would be left with less than 10GB free.

Run with no arguments to generate the first five. Pass --corrupt, --valid, --geojson, --wide or
--ini to generate just one, or --huge for the 100GB file - the 4GB corrupt file takes minutes, so the others alone are handy for
quick UI iteration.
"""
import argparse, math, os, random, shutil, time

OUT_DIR = os.path.expanduser("~/testData")
CORRUPT_OUT = os.path.join(OUT_DIR, "corrupt-sample-4gb.json")
VALID_OUT = os.path.join(OUT_DIR, "valid-sample-50mb.json")
GEOJSON_OUT = os.path.join(OUT_DIR, "geojson-sample-25mb.json")
WIDE_OUT = os.path.join(OUT_DIR, "wide-object-sample.json")

INI_OUT = os.path.join(OUT_DIR, "config-sample.ini")
INI_SECTIONS = 2_000
INI_LONG_VALUE_EVERY = 50
INI_LONG_VALUE_CHARS = 20_000

HUGE_DEFAULT_GB = 100
HUGE_TEMPLATE_LINES = 40_000
HUGE_MIN_FREE_AFTER = 10 * 1024**3
HUGE_BLOCK_MARK = b"#BLOCK##"  # 8 bytes, replaced by the block number padded with spaces

# Non-ASCII keys and values for the valid and huge files, written as raw UTF-8 unless marked. Chosen to
# cover what tends to break text handling: 2-, 3- and 4-byte UTF-8, right-to-left scripts,
# combining marks, emoji ZWJ sequences and flags, invisible characters, and JSON \u escapes
# (including an escaped surrogate pair) that decode to the same kind of text.
SAMPLE_UNICODE_KEYS = (
    "名前", "città", "größe", "имя", "اسم", "שם", "emoji_🚀", "日本語キー", "naïve", "ключ_🔑",
    "\\u00e9t\\u00e9",          # escaped: été
)
SAMPLE_UNICODE_VALUES = (
    "こんにちは世界", "Ünïcödé façade", "Ελληνικά γράμματα", "مرحبا بالعالم", "שלום עולם",
    "한국어 텍스트", "हिन्दी पाठ", "ภาษาไทย", "😀🎉🚀", "👩‍💻 ZWJ sequence", "🇬🇧🇯🇵 flags",
    "é combining acute",  # e + U+0301
    "zero​width space",    # U+200B
    "no break space",      # U+00A0
    "line separator",      # U+2028, legal unescaped in JSON
    "escaped \\u00fcml\\u00e4ut and \\ud83d\\ude00",  # escaped: ümläut and 😀 (surrogate pair)
)

WIDE_OUTER_KEYS = 3_000_000
WIDE_INNER_KEYS = 1_000_000

CORRUPT_TARGET = 4 * 1024**3
CORRUPT_AT = 3 * 1024**3
CHECKPOINT_EVERY = 250 * 1024**2

VALID_TARGET = 50 * 1024**2
VALID_OBJECT_COUNT = 15
HUGE_STRING_OBJECT_INDEX = 1  # the "second object"
HUGE_STRING_SIZE = 10 * 1024**2

random.seed(42)
WORDS = ("alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima "
         "mike november oscar papa quebec romeo sierra tango uniform victor whiskey "
         "xray yankee zulu payload sensor telemetry packet frame buffer offset segment").split()


def make_object(i):
    name = f"item-{i}-{random.choice(WORDS)}-{random.choice(WORDS)}"
    value = round(random.uniform(0, 100000), 3)
    active = "true" if random.random() < 0.5 else "false"
    tags = ",".join(f'"{random.choice(WORDS)}"' for _ in range(random.randint(1, 4)))
    x, y = random.randint(0, 1000), random.randint(0, 1000)
    return (
        f'{{"id":{i},"name":"{name}","value":{value},"active":{active},'
        f'"tags":[{tags}],"nested":{{"x":{x},"y":{y}}}}}'
    )


def make_corrupt_json():
    start = time.time()
    written = 0
    i = 0
    next_checkpoint = CHECKPOINT_EVERY
    corrupted = False

    with open(CORRUPT_OUT, "wb", buffering=1024 * 1024) as f:
        def put(data: bytes):
            nonlocal written
            f.write(data)
            written += len(data)

        put(b"[\n")

        while written < CORRUPT_TARGET - 64:
            if not corrupted and written >= CORRUPT_AT:
                # A lone '#' is not valid anywhere in JSON grammar (not whitespace, not a
                # value start, not a valid separator) - Utf8JsonReader faults on it exactly
                # here, having already published every token before it.
                put(b"#CORRUPTED_HERE#\n")
                corrupted = True

            obj = make_object(i).encode()
            i += 1
            put(obj + b",\n")

            if written >= next_checkpoint:
                print(f"{written / 1024**3:.2f} GiB written ({time.time() - start:.0f}s)"
                      + ("  [past corruption point]" if corrupted else ""), flush=True)
                next_checkpoint += CHECKPOINT_EVERY

        # Deliberately no closing ']' - the reader already faulted well before EOF, so the
        # tail's shape doesn't matter.

    print(f"DONE: {written:,} bytes in {time.time() - start:.0f}s -> {CORRUPT_OUT}", flush=True)
    print(f"Corruption injected at byte offset {CORRUPT_AT:,} (~3GiB)", flush=True)


def filler_string(n):
    """Ascii filler of exactly n chars - plain words/spaces, safe inside a JSON string with
    no escaping needed."""
    if n <= 0:
        return ""
    parts = []
    total = 0
    while total < n:
        w = random.choice(WORDS)
        parts.append(w)
        total += len(w) + 1
    return " ".join(parts)[:n]


NESTED_DEPTH = 4
NOW_MS = int(time.time() * 1000)


def make_nested_object(level):
    """A single object nested NESTED_DEPTH levels deep via a "child" property at each
    level - level 1 is the outermost, level NESTED_DEPTH has no further child."""
    x, y = random.randint(0, 1000), random.randint(0, 1000)
    if level >= NESTED_DEPTH:
        return f'{{"level":{level},"x":{x},"y":{y}}}'
    return f'{{"level":{level},"x":{x},"y":{y},"child":{make_nested_object(level + 1)}}}'


def build_base_fields(i):
    name = f"item-{i}-{random.choice(WORDS)}-{random.choice(WORDS)}"
    value = round(random.uniform(0, 100000), 3)
    active = "true" if random.random() < 0.5 else "false"
    # JS-formatted date: a `Date.now()`-shaped epoch-ms number (13 digits at present-day
    # values), which the app's date-hint classifier auto-detects as JsMilliseconds.
    date_ms = NOW_MS - random.randint(0, 365 * 24 * 3600 * 1000)
    tags = ",".join(f'"{random.choice(WORDS)}"' for _ in range(random.randint(2, 6)))
    numbers = ",".join(str(round(random.uniform(0, 1000), 2)) for _ in range(random.randint(3, 6)))
    objects = ",".join(
        f'{{"id":{j},"label":"{random.choice(WORDS)}"}}' for j in range(random.randint(2, 5))
    )
    nested = make_nested_object(1)
    return name, value, active, date_ms, tags, numbers, objects, nested


def unicode_object(i):
    """Every sample value under a sample key, the keys rotated by i so each object pairs them
    differently; once the keys run out they repeat with a suffix, keeping each one unique. No
    random draws, so the seeded stream - and every other file - is unchanged."""
    keys, values = SAMPLE_UNICODE_KEYS, SAMPLE_UNICODE_VALUES
    members = []
    for k, value in enumerate(values):
        key = keys[(i + k) % len(keys)] + ("" if k < len(keys) else f"_{k}")
        members.append(f'"{key}":"{value}"')
    return "{" + ",".join(members) + "}"


def render_object(i, fields, extra_key, extra_val):
    name, value, active, date_ms, tags, numbers, objects, nested = fields
    return (
        f'{{"id":{i},"name":"{name}","value":{value},"active":{active},"date":{date_ms},'
        f'"tags":[{tags}],"numbers":[{numbers}],"objects":[{objects}],'
        f'"nested":{nested},"i18n":{unicode_object(i)},'
        f'"{extra_key}":{extra_val}}}'
    )


def make_valid_sample():
    """15 objects mixing string/number/array/nested-object properties. Object[1] gets a
    ~10MB string instead of padding, to exercise overflow rendering for one pathologically
    long value. The other 14 get a "padding" string sized (in a first sizing pass) so the
    whole file lands close to VALID_TARGET regardless of how long the random fields ended
    up being."""
    start = time.time()
    fields_by_index = [build_base_fields(i) for i in range(VALID_OBJECT_COUNT)]

    huge_val = '"' + ("x" * HUGE_STRING_SIZE) + '"'
    base_lengths = []
    for i in range(VALID_OBJECT_COUNT):
        if i == HUGE_STRING_OBJECT_INDEX:
            extra_key, extra_val = "hugeString", huge_val
        else:
            extra_key, extra_val = "padding", '""'
        base_lengths.append(len(render_object(i, fields_by_index[i], extra_key, extra_val)))

    structural = len("[\n") + len("\n]\n") + (VALID_OBJECT_COUNT - 1) * len(",\n")
    leftover = max(0, VALID_TARGET - sum(base_lengths) - structural)
    padding_per_object = leftover // (VALID_OBJECT_COUNT - 1)

    written = 0
    with open(VALID_OUT, "w", encoding="utf-8", buffering=1024 * 1024) as f:
        f.write("[\n")
        for i in range(VALID_OBJECT_COUNT):
            if i == HUGE_STRING_OBJECT_INDEX:
                extra_key, extra_val = "hugeString", huge_val
            else:
                extra_key, extra_val = "padding", '"' + filler_string(padding_per_object) + '"'

            obj = render_object(i, fields_by_index[i], extra_key, extra_val)
            f.write(obj)
            written += len(obj)
            f.write(",\n" if i < VALID_OBJECT_COUNT - 1 else "\n")
        f.write("]\n")

    print(f"DONE: valid sample ~{written / 1024**2:.1f} MiB, {VALID_OBJECT_COUNT} objects, "
          f"object[{HUGE_STRING_OBJECT_INDEX}] carries a {HUGE_STRING_SIZE / 1024**2:.0f}MiB "
          f"string -> {VALID_OUT}  ({time.time() - start:.1f}s)", flush=True)


# ---------------------------------------------------------------------------
# GeoJSON sample
# ---------------------------------------------------------------------------

GEOJSON_TARGET = 25 * 1024**2

# Every coordinate is generated strictly inside this box, which is what lets the collection's
# top-level "bbox" be both truthful and written before the features it bounds - the file streams
# out, so a bbox computed from the features could only be appended after them.
REGION = (-114.30, 50.85, -113.80, 51.20)  # (min lon, min lat, max lon, max lat)

# Deliberately long enough to make the point the example schema argues: a title on this array's
# "items" would repeat down every one of these rows.
LINE_MIN_POSITIONS, LINE_MAX_POSITIONS = 4_000, 20_000

GEOMETRY_KINDS = ("Point", "LineString", "Polygon", "MultiPoint",
                  "MultiLineString", "MultiPolygon", "GeometryCollection")
GEOMETRY_WEIGHTS = (18, 34, 22, 6, 6, 10, 4)

CATEGORIES = ("trail", "watercourse", "parcel", "survey-marker", "utility-run", "flood-zone")


def _clamp(value, low, high):
    return low if value < low else high if value > high else value


def _position(lon, lat, elevation=False):
    """Rendered as [longitude, latitude] or [longitude, latitude, elevation] - the order the
    example schema's prefixItems documents."""
    if elevation:
        return f"[{lon:.5f},{lat:.5f},{random.uniform(900, 3400):.1f}]"
    return f"[{lon:.5f},{lat:.5f}]"


def _random_point():
    return random.uniform(REGION[0], REGION[2]), random.uniform(REGION[1], REGION[3])


def _walk(count, elevation=False):
    """A random walk of `count` positions, each step small so the result reads like a real
    track rather than noise, and clamped so it never leaves REGION."""
    lon, lat = _random_point()
    out = []
    for _ in range(count):
        lon = _clamp(lon + random.uniform(-0.0015, 0.0015), REGION[0], REGION[2])
        lat = _clamp(lat + random.uniform(-0.0015, 0.0015), REGION[1], REGION[3])
        out.append(_position(lon, lat, elevation))
    return out


def _ring(radius=0.01, points=None):
    """A closed ring: GeoJSON requires the last position to repeat the first."""
    lon, lat = _random_point()
    points = points or random.randint(5, 12)
    vertices = []
    for k in range(points):
        angle = (k / points) * 6.283185
        vertices.append(_position(
            _clamp(lon + radius * random.uniform(0.6, 1.4) * math.cos(angle), REGION[0], REGION[2]),
            _clamp(lat + radius * random.uniform(0.6, 1.4) * math.sin(angle), REGION[1], REGION[3])))
    vertices.append(vertices[0])
    return "[" + ",".join(vertices) + "]"


def _geometry(kind, depth=0):
    """One geometry object. GeometryCollection recurses (never into another collection), which
    is what exercises the example schema's recursive $ref."""
    if kind == "Point":
        lon, lat = _random_point()
        return f'{{"type":"Point","coordinates":{_position(lon, lat, elevation=True)}}}'

    if kind == "MultiPoint":
        points = ",".join(_position(*_random_point()) for _ in range(random.randint(3, 40)))
        return f'{{"type":"MultiPoint","coordinates":[{points}]}}'

    if kind == "LineString":
        path = ",".join(_walk(random.randint(LINE_MIN_POSITIONS, LINE_MAX_POSITIONS), elevation=True))
        return f'{{"type":"LineString","coordinates":[{path}]}}'

    if kind == "MultiLineString":
        lines = ",".join("[" + ",".join(_walk(random.randint(200, 2_000))) + "]"
                         for _ in range(random.randint(2, 5)))
        return f'{{"type":"MultiLineString","coordinates":[{lines}]}}'

    if kind == "Polygon":
        # An outer ring, then any number of holes - hence the extra level of nesting.
        rings = [_ring(radius=0.02)] + [_ring(radius=0.004) for _ in range(random.randint(0, 2))]
        return f'{{"type":"Polygon","coordinates":[{",".join(rings)}]}}'

    if kind == "MultiPolygon":
        polygons = ",".join("[" + _ring(radius=0.012) + "]" for _ in range(random.randint(2, 6)))
        return f'{{"type":"MultiPolygon","coordinates":[{polygons}]}}'

    members = ",".join(
        _geometry(random.choice(("Point", "LineString", "Polygon")), depth + 1)
        for _ in range(random.randint(2, 4)))
    return f'{{"type":"GeometryCollection","geometries":[{members}]}}'


def _feature(i):
    kind = random.choices(GEOMETRY_KINDS, weights=GEOMETRY_WEIGHTS, k=1)[0]

    # Free-form under RFC 7946, so the example schema labels these via additionalProperties.
    # recordedAt is epoch-ms so the date hint and the schema label land on the same row.
    recorded = NOW_MS - random.randint(0, 5 * 365 * 24 * 3600 * 1000)
    properties = (
        f'{{"name":"{random.choice(CATEGORIES)}-{i}-{random.choice(WORDS)}",'
        f'"category":"{random.choice(CATEGORIES)}",'
        f'"recordedAt":{recorded},'
        f'"surveyed":{"true" if random.random() < 0.5 else "false"},'
        f'"accuracyM":{random.uniform(0.5, 25):.2f},'
        f'"notes":"{filler_string(random.randint(20, 160))}"}}'
    )

    # A per-feature bbox, same fixed [min lon, min lat, max lon, max lat] layout as the
    # collection's. Kept inside REGION rather than computed from the geometry: it only has to be
    # a plausible fixed-layout tuple for the prefixItems labels to sit on.
    lon, lat = _random_point()
    max_lon = _clamp(lon + random.uniform(0.001, 0.05), REGION[0], REGION[2])
    max_lat = _clamp(lat + random.uniform(0.001, 0.05), REGION[1], REGION[3])
    bbox = f"[{lon:.5f},{lat:.5f},{max_lon:.5f},{max_lat:.5f}]"

    return (f'{{"type":"Feature","id":{i},"bbox":{bbox},'
            f'"geometry":{_geometry(kind)},"properties":{properties}}}')


def make_geojson_sample():
    start = time.time()
    written = 0
    counts = {kind: 0 for kind in GEOMETRY_KINDS}

    with open(GEOJSON_OUT, "w", buffering=1024 * 1024) as f:
        def put(text):
            nonlocal written
            f.write(text)
            written += len(text)

        put('{\n"type":"FeatureCollection",\n')
        put(f'"bbox":[{REGION[0]},{REGION[1]},{REGION[2]},{REGION[3]}],\n')
        put('"features":[\n')

        i = 0
        while written < GEOJSON_TARGET:
            feature = _feature(i)
            counts[feature.split('"type":"', 2)[2].split('"')[0]] += 1
            put(("" if i == 0 else ",\n") + feature)
            i += 1

        put("\n]\n}\n")

    summary = ", ".join(f"{kind} {counts[kind]}" for kind in GEOMETRY_KINDS if counts[kind])
    print(f"DONE: geojson sample ~{written / 1024**2:.1f} MiB, {i:,} features "
          f"({summary}) -> {GEOJSON_OUT}  ({time.time() - start:.1f}s)", flush=True)


def make_wide_object_sample():
    """Deterministic - no random draws - so it never shifts the other files' seeded stream.

    Keys are zero-padded so every member is the same size and a key's position in the file is
    proportional to its number: k0000000 is the first name read, k2999999 the last. The last
    outer member is the one that nests, so the slowest path pays for two full scans in a row.
    """
    start = time.time()
    outer_last = f"k{WIDE_OUTER_KEYS - 1:07d}"
    inner_last = f"n{WIDE_INNER_KEYS - 1:07d}"

    with open(WIDE_OUT, "w", buffering=4 * 1024 * 1024) as f:
        f.write("{\n")
        batch = []
        for i in range(WIDE_OUTER_KEYS - 1):
            batch.append(f'"k{i:07d}":{{"id":{i},"v":{i % 997}}},\n')
            if len(batch) == 10_000:
                f.write("".join(batch))
                batch.clear()
        f.write("".join(batch))
        batch.clear()

        f.write(f'"{outer_last}":{{"inner":{{\n')
        for i in range(WIDE_INNER_KEYS - 1):
            batch.append(f'"n{i:07d}":{{"id":{i}}},\n')
            if len(batch) == 10_000:
                f.write("".join(batch))
                batch.clear()
        f.write("".join(batch))
        # An array at the bottom, so "view as table" and Back - which navigates by path - can
        # be timed from the deepest point too.
        f.write(f'"{inner_last}":{{"rows":[{{"a":1,"b":2}},{{"a":3,"b":4}}]}}\n')
        f.write("}}\n}\n")

    size = os.path.getsize(WIDE_OUT)
    print(f"DONE: wide object sample ~{size / 1024**2:.1f} MiB -> {WIDE_OUT}  ({time.time() - start:.1f}s)")
    print("  paths, fastest to slowest:")
    print("    $.k0000000                      first key")
    print("    $.k1500000                      middle of the root")
    print(f"    $.{outer_last}                      last key of the root")
    print(f"    $.{outer_last}.inner.{inner_last}.rows   both scans in full, then into an array", flush=True)


def make_ini_sample():
    """Deterministic - no random draws - so it never shifts the other files' seeded stream.

    Each section cycles through the same set of line shapes, so whatever a screen of it shows,
    every rule the config lexer has is on it. The comments in the file say what each line is
    there to check.
    """
    start = time.time()
    keywords = ("true", "false", "yes", "no", "on", "off")

    with open(INI_OUT, "w", buffering=1024 * 1024) as f:
        f.write("; Argonaut config colouring sample - generated by scripts/make-test-json.py --ini\n")
        f.write("# Both comment characters start a comment line.\n")
        f.write("! So does a bang, the .properties comment.\n\n")
        f.write("top_level_key = before any section\n\n")

        for i in range(INI_SECTIONS):
            f.write(f"[section.{i:04d}]\n")
            f.write(f"name = service-{i}\n")
            f.write(f"port={8000 + i}\n")
            f.write(f"ratio = {i / 7:.4f}\n")
            f.write(f"negative = -{i}e3\n")
            f.write(f"enabled = {keywords[i % len(keywords)]}\n")
            f.write(f"label = \"Quoted # not a comment ; nor this\"\n")
            f.write(f"single = 'single quoted = still the value'\n")
            f.write(f"escaped = \"say \\\"hi\\\" to {i}\"\n")
            f.write(f"url = https://example.com/items/{i}#anchor ; the # in the URL is not a comment\n")
            f.write(f"path = C:\\Program Files\\App\\{i}\\config.ini\n")
            f.write(f"start_time = {i % 24:02d}:{i % 60:02d}\n")
            f.write(f"version = v1.{i}-beta\n")
            f.write(f"colon_style: properties files may use colon-space\n")
            f.write(f"timeout = 30 # seconds\n")
            f.write(f"\"quoted key {i}\" = 1\n")
            f.write(f"  indented_key   =   padded value   ; trailing comment\n")
            f.write(f"empty =\n")
            f.write(f"a line with no separator at all\n")
            if i % INI_LONG_VALUE_EVERY == 0:
                # One long line: rows past the colouring lookback show plain until the next line.
                filler = ",".join(f"item{n}" for n in range(INI_LONG_VALUE_CHARS // 8))
                f.write(f"long_list = {filler[:INI_LONG_VALUE_CHARS]}\n")
            if i % 10 == 0:
                f.write(f"[[array.table.{i}]]\n")
                f.write(f"member = {i}\n")
            f.write("\n")

    size = os.path.getsize(INI_OUT)
    print(f"DONE: ini sample ~{size / 1024**2:.1f} MiB, {INI_SECTIONS:,} sections -> {INI_OUT}  "
          f"({time.time() - start:.1f}s)", flush=True)


def _huge_template():
    """One block of varied NDJSON lines, every one carrying HUGE_BLOCK_MARK in its "block" field.

    Built once from its own seeded Random, so it never shifts the other files' shared stream.
    Stamping each copy with its block number keeps every line unique as (block, seq) without
    formatting hundreds of millions of lines one at a time.
    """
    rng = random.Random(2026)
    statuses = ("active", "pending", "closed", "failed", "retrying")
    lines = []
    for seq in range(HUGE_TEMPLATE_LINES):
        tags = ",".join(f'"{rng.choice(WORDS)}"' for _ in range(rng.randint(0, 5)))
        readings = ",".join(f"{rng.uniform(-50, 150):.3f}" for _ in range(rng.randint(1, 8)))
        note = " ".join(rng.choice(WORDS) for _ in range(rng.randint(3, 25)))
        if seq % 97 == 0:
            note += ' with \\"escaped quotes\\" and a tab\\t'
        # Every line has a non-ASCII label; every 7th also an object whose keys are non-ASCII.
        label = SAMPLE_UNICODE_VALUES[seq % len(SAMPLE_UNICODE_VALUES)]
        i18n = ""
        if seq % 7 == 0:
            members = ",".join(
                f'"{SAMPLE_UNICODE_KEYS[(seq + k) % len(SAMPLE_UNICODE_KEYS)]}":'
                f'"{SAMPLE_UNICODE_VALUES[(seq * 3 + k) % len(SAMPLE_UNICODE_VALUES)]}"'
                for k in range(3))
            i18n = f',"i18n":{{{members}}}'
        lines.append(
            f'{{"block":{HUGE_BLOCK_MARK.decode()},"seq":{seq},'
            f'"ts":{1_700_000_000_000 + seq * 1_337},'
            f'"status":"{rng.choice(statuses)}","active":{"true" if rng.random() < 0.5 else "false"},'
            f'"score":{rng.uniform(0, 1):.6f},"tags":[{tags}],'
            f'"geo":{{"lat":{rng.uniform(-90, 90):.5f},"lon":{rng.uniform(-180, 180):.5f}}},'
            f'"readings":[{readings}],"note":"{note}","label":"{label}"{i18n},'
            f'"parent":{"null" if seq % 5 else seq - 1}}}\n'
        )
    return "".join(lines).encode("utf-8")


def make_huge_ndjson(target_gb):
    out = os.path.join(OUT_DIR, f"huge-sample-{target_gb:g}gb.ndjson")
    target = int(target_gb * 1024**3)
    free = shutil.disk_usage(OUT_DIR).free
    if free - target < HUGE_MIN_FREE_AFTER:
        print(f"REFUSED: {target / 1024**3:.0f} GiB would leave {(free - target) / 1024**3:.1f} GiB free "
              f"in {OUT_DIR} - less than {HUGE_MIN_FREE_AFTER / 1024**3:.0f} GiB. Pass a smaller --huge-gb.",
              flush=True)
        return

    start = time.time()
    template = _huge_template()
    blocks = max(1, target // len(template))
    needles = {blocks // 2: "argonaut-needle-50pct", max(0, blocks - 2): "argonaut-needle-end"}
    found = {}
    written = 0
    next_report = 5 * 1024**3

    with open(out, "wb", buffering=16 * 1024 * 1024) as f:
        for block in range(blocks):
            # Padded with spaces, not zeros: JSON forbids a leading 0, but allows the whitespace.
            f.write(template.replace(HUGE_BLOCK_MARK, b"%8d" % block))
            written += len(template)

            if block in needles:
                found[needles[block]] = written
                needle = f'{{"block":{block},"seq":-1,"needle":"{needles[block]}"}}\n'.encode()
                f.write(needle)
                written += len(needle)

            if written >= next_report:
                rate = written / max(time.time() - start, 1e-6) / 1024**2
                print(f"  {written / 1024**3:.0f} GiB written ({rate:.0f} MiB/s)", flush=True)
                next_report += 5 * 1024**3

    lines = blocks * HUGE_TEMPLATE_LINES + len(needles)
    print(f"DONE: huge ndjson ~{written / 1024**3:.1f} GiB, {lines:,} lines -> {out}  "
          f"({time.time() - start:.1f}s)")
    for name, offset in found.items():
        print(f"  {name} at byte {offset:,}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--corrupt", action="store_true", help="generate only the 4GB corrupt file")
    parser.add_argument("--valid", action="store_true", help="generate only the 50MB valid file")
    parser.add_argument("--geojson", action="store_true", help="generate only the 25MB GeoJSON file")
    parser.add_argument("--wide", action="store_true", help="generate only the ~130MB million-key object file")
    parser.add_argument("--ini", action="store_true", help="generate only the ~2MB INI config file")
    parser.add_argument("--huge", action="store_true",
                        help="generate the 100GB NDJSON stress file (never part of a bare run)")
    parser.add_argument("--huge-gb", type=float, default=HUGE_DEFAULT_GB, metavar="GB",
                        help=f"size of the --huge file in GiB (default {HUGE_DEFAULT_GB})")
    args = parser.parse_args()

    if args.huge:
        os.makedirs(OUT_DIR, exist_ok=True)
        make_huge_ndjson(args.huge_gb)
        if not (args.corrupt or args.valid or args.geojson or args.wide or args.ini):
            return

    everything = not (args.corrupt or args.valid or args.geojson or args.wide or args.ini)

    os.makedirs(OUT_DIR, exist_ok=True)
    if args.valid or everything:
        make_valid_sample()
    if args.corrupt or everything:
        make_corrupt_json()
    # Last, so adding it leaves the other two files byte-identical to previous runs (they draw
    # from the same seeded random stream).
    if args.geojson or everything:
        make_geojson_sample()
    if args.wide or everything:
        make_wide_object_sample()
    if args.ini or everything:
        make_ini_sample()


if __name__ == "__main__":
    main()
