#!/usr/bin/env python3
"""Generates test JSON fixtures for the Argonaut JSON viewer:

  - corrupt-sample-4gb.json: ~4GB, valid up to ~3GB in, then structurally invalid - for
    testing JsonStructureIndex's partial-index / IndexFailure path (a scan that stops with
    items already published).
  - valid-sample-50mb.json: ~50MB, top-level array of 15 well-formed objects, each with
    string/number/bool/array-of-string/array-of-number/array-of-object properties, an "i18n"
    object of non-ASCII keys and values (scripts, emoji, combining marks, invisible characters,
    U+2028 and JSON unicode escapes), a
    JS-epoch-ms "date" property, a "hints" object of values the tree's value hints recognise
    (and near misses they must not), and a 4-object-deep nested hierarchy - for baseline
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
    the colouring lookback. Every section also carries values the raw view hints - a colour, an
    address, a JWT, a cron schedule, an ISO date - and every 10th a Base64 picture, plus near
    misses that must stay plain.
  - config-sample.yaml: ~1MB YAML of services for the same colouring and hints in YAML's shape -
    nested mappings, lists, quoted and bare scalars, block scalars and comments.

  - citygml-sample-500mb.xml: ~500MB CityGML 2.0 city model for the XML view - eleven
    namespaces, attributes on most elements, a root of ~70,000 members nested ~15 deep, comments,
    processing instructions, CDATA, entity and character references, a namespace declared below
    the root, a default-namespace spelling of the same elements, and one ~20MB text node. The
    XPaths worth trying are printed when it is written. Only written when --xml is passed, never
    by a bare run, to spare the disk.

  - huge-sample-<N>gb.ndjson: 100GB of NDJSON by default (size set by --huge-gb), far past any laptop's
    RAM, for stress-testing indexing, scrolling, search and memory use on a file that cannot be
    held in memory. Two needle lines - at 50% and near the end - give search and go-to something
    unique to find; their byte offsets are printed. Only ever written when --huge is passed, never
    by a bare run, and refused when the disk would be left with less than 10GB free.

Run with no arguments to generate the first five. Pass --corrupt, --valid, --geojson, --wide,
--ini or --yaml to generate just one, --xml for the 500MB CityGML file, or --huge for the 100GB file - the 4GB corrupt file takes minutes, so the others alone are handy for
quick UI iteration.
"""
import argparse, base64, datetime, json, math, os, random, shutil, time

OUT_DIR = os.path.expanduser("~/testData")
CORRUPT_OUT = os.path.join(OUT_DIR, "corrupt-sample-4gb.json")
VALID_OUT = os.path.join(OUT_DIR, "valid-sample-50mb.json")
GEOJSON_OUT = os.path.join(OUT_DIR, "geojson-sample-25mb.json")
WIDE_OUT = os.path.join(OUT_DIR, "wide-object-sample.json")

INI_OUT = os.path.join(OUT_DIR, "config-sample.ini")
INI_SECTIONS = 2_000
INI_LONG_VALUE_EVERY = 50
INI_LONG_VALUE_CHARS = 20_000
YAML_OUT = os.path.join(OUT_DIR, "config-sample.yaml")
YAML_SERVICES = 1_500
XML_OUT = os.path.join(OUT_DIR, "citygml-sample-500mb.xml")
XML_TARGET = 500 * 1024**2
XML_LONG_POSLIST_POINTS = 600_000   # about 20MB of coordinates in one text node

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


def _b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def _jwt(claims):
    """A JWT-shaped token: real base64url header and claims, and a signature that is only bytes -
    nothing verifies it, and a hint only reads the first two parts."""
    header = _b64url(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    payload = _b64url(json.dumps(claims, separators=(",", ":")).encode())
    return f"{header}.{payload}.{_b64url(bytes(range(32)))}"


def _png(width, height, pixel, level=9):
    """A real PNG - RGBA, one filter byte per row - with pixel(x, y) giving each (r, g, b, a), so
    a hint's card can show it, not just name it."""
    import struct, zlib
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    rows = b"".join(b"\x00" + b"".join(bytes(pixel(x, y)) for x in range(width)) for y in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows, level)) + chunk(b"IEND", b"")


def _icon_pixel(x, y):
    """A 32x32 badge: a blue-to-green field with a white ring."""
    d = ((x - 15.5) ** 2 + (y - 15.5) ** 2) ** 0.5
    if 9 <= d <= 12:
        return (255, 255, 255, 255)
    return (37 + x * 2, 99 + y * 3, 235 - x * 3, 255 if d < 15.5 else 0)


SMALL_PNG = _png(32, 32, _icon_pixel)
# The first bytes of a gzip stream, which is all a Base64 hint looks at to say what the payload is;
# the rest is filler of the right length.
GZIP_HEADER = bytes([0x1F, 0x8B, 0x08, 0x00])
HINT_LARGE_BASE64_OBJECT_INDEX = 2
HINT_LARGE_BASE64_BYTES = 3 * 1024**2


def hint_samples(i):
    """Values for every kind of value hint, varied by i, plus near misses that must stay plain.
    No random draws, so the seeded stream - and every other file - is unchanged. Dates are
    relative to when the file is written, so "3 days ago" is true on the day it is made."""
    now = datetime.datetime.fromtimestamp(NOW_MS / 1000, tz=datetime.timezone.utc).replace(microsecond=0)
    zones = (datetime.timezone.utc, datetime.timezone(datetime.timedelta(hours=1)),
             datetime.timezone(datetime.timedelta(hours=-5)), datetime.timezone(datetime.timedelta(hours=5, minutes=30)))
    zone = zones[i % len(zones)]
    past = (now - datetime.timedelta(days=3 + i, hours=i)).astimezone(zone)
    future = (now + datetime.timedelta(days=30 * (i + 1))).astimezone(zone)

    def iso(moment, millis=False):
        text = moment.isoformat(timespec="milliseconds" if millis else "seconds")
        return text.replace("+00:00", "Z")

    exp = int((now + datetime.timedelta(hours=1 - 2 * (i % 2))).timestamp())  # odd items expired
    colours = ("#1E90FF", "#ff8800", "#2E8B57CC", "rgb(255, 99, 71)", "hsl(210, 60%, 45%)")
    crons = ("*/15 * * * *", "0 9 * * 1-5", "30 2 1 * *", "0 0 * * 0", "15 14 1 1 *")
    embedded = json.dumps({"event": "order.created", "id": f"ord_{88000 + i}",
                           "lines": [{"sku": f"A-{100 + i}", "qty": i % 4 + 1}], "paid": i % 2 == 0},
                          separators=(",", ":"))
    payload = SMALL_PNG if i % 2 == 0 else GZIP_HEADER + bytes((i * 7 + k) % 256 for k in range(96))
    small_base64 = base64.b64encode(payload).decode()

    members = {
        "isoInstant": iso(past),
        "isoInstantMillis": iso(past + datetime.timedelta(minutes=17), millis=True),
        "isoFuture": iso(future),
        "isoDate": future.date().isoformat(),
        "isoLocalDateTime": past.replace(tzinfo=None).isoformat(sep=" ", timespec="minutes"),
        "colour": colours[i % len(colours)],
        "jwt": _jwt({"sub": f"user-{1000 + i}", "iat": exp - 3600, "exp": exp}),
        "cron": crons[i % len(crons)],
        "jsonInString": embedded,
        "base64": small_base64,
        "url": ("https://example.com/orders", f"https://shop.example.co.uk/basket?item={i}&ref=email#pay",
                "mailto:support@example.com?subject=Order%20" + str(88000 + i))[i % 3],
        "urlMisleading": "https://good.example.com@evil.example.net/login",
        "urlUnsafe": "javascript:alert(1)",
        "urlInText": f"see https://example.com/help/{i} for details",
        "notADate": "2026-02-30",
        "notAColour": ("#12345", "#f80", "#123")[i % 3],
        "notJson": "{ looks like json, but is not }",
        "notBase64": "Zm9v!YmFy",
    }
    if i == HINT_LARGE_BASE64_OBJECT_INDEX:
        # Stored rather than compressed, so the picture is as big as the payload it stands for.
        side = int((HINT_LARGE_BASE64_BYTES / 4) ** 0.5)
        members["base64Large"] = base64.b64encode(
            _png(side, side, lambda x, y: (x % 256, y % 256, (x ^ y) % 256, 255), level=0)).decode()
    return json.dumps(members, ensure_ascii=False, separators=(",", ":"))


def render_object(i, fields, extra_key, extra_val):
    name, value, active, date_ms, tags, numbers, objects, nested = fields
    return (
        f'{{"id":{i},"name":"{name}","value":{value},"active":{active},"date":{date_ms},'
        f'"tags":[{tags}],"numbers":[{numbers}],"objects":[{objects}],'
        f'"nested":{nested},"i18n":{unicode_object(i)},"hints":{hint_samples(i)},'
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
            for line in config_hint_lines(i, "ini"):
                f.write(line + "\n")
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


def config_hint_lines(i, style):
    """Config pairs whose values the raw view hints, varied by i, and near misses that must stay
    plain. INI writes key = value, YAML key: value. A bare #colour is a comment in both, so
    colours are quoted or written as rgb()."""
    sep = " = " if style == "ini" else ": "
    now_s = NOW_MS // 1000
    colours = ('"#1E90FF"', "rgb(255, 99, 71)", '"#2E8B57CC"', "hsl(210, 60%, 45%)", '"#ff8800"')
    crons = ('"*/15 * * * *"', '"0 9 * * 1-5"', '"30 2 1 * *"')
    released = datetime.datetime.fromtimestamp(now_s - 86_400 * (i % 400 + 1), tz=datetime.timezone.utc)
    lines = [
        f"colour{sep}{colours[i % len(colours)]}",
        f"homepage{sep}https://example.com/services/{i}",
        f"support{sep}mailto:support+{i}@example.com",
        f"token{sep}{_jwt({'sub': f'svc-{i}', 'exp': now_s + 3600 * (i % 48 - 24)})}",
        f"schedule{sep}{crons[i % len(crons)]}",
        f"released{sep}{released.isoformat().replace('+00:00', 'Z')}",
        f"not_a_colour{sep}\"#12345\"",
        f"not_a_date{sep}2026-02-30",
        f"not_a_url{sep}javascript:alert({i})",
    ]
    if i % 10 == 0:
        lines.append(f"logo{sep}{base64.b64encode(SMALL_PNG).decode()}")
    return lines


def make_yaml_sample():
    """Deterministic - no random draws - so it never shifts the other files' seeded stream.

    A list of services, each the same set of YAML shapes - nested mappings, lists, quoted and bare
    scalars, a block scalar, comments - with the values the raw view hints among them."""
    start = time.time()
    with open(YAML_OUT, "w", buffering=1024 * 1024) as f:
        f.write("# Argonaut YAML sample - generated by scripts/make-test-json.py --yaml\n")
        f.write("---\nversion: 2\nservices:\n")
        for i in range(YAML_SERVICES):
            f.write(f"  - name: service-{i}  # a trailing comment\n")
            f.write(f"    port: {8000 + i}\n")
            f.write(f"    enabled: {'true' if i % 2 else 'false'}\n")
            f.write(f"    title: 'Service {i}: single quoted, with a colon'\n")
            for line in config_hint_lines(i, "yaml"):
                f.write(f"    {line}\n")
            f.write("    tags:\n")
            f.write(f"      - tier-{i % 3}\n      - region-{('eu', 'us', 'ap')[i % 3]}\n")
            f.write("    description: |\n")
            f.write(f"      Block scalar body for service {i}; see https://example.com/docs/{i}\n")
            f.write("      key: value lines in here are body text, not pairs\n")
            f.write("    limits: {cpu: 2, memory: 512Mi}\n")
        f.write("...\n")

    size = os.path.getsize(YAML_OUT)
    print(f"DONE: yaml sample ~{size / 1024**2:.1f} MiB, {YAML_SERVICES:,} services -> {YAML_OUT}  "
          f"({time.time() - start:.1f}s)", flush=True)


# CityGML 2.0 (OGC 12-019): the namespaces a real city model carries. Prefixes are the
# conventional ones; Energy ADE is declared locally on the buildings that use it, as an
# Application Domain Extension is in the wild.
CITYGML_NAMESPACES = (
    ("core", "http://www.opengis.net/citygml/2.0"),
    ("bldg", "http://www.opengis.net/citygml/building/2.0"),
    ("gen", "http://www.opengis.net/citygml/generics/2.0"),
    ("app", "http://www.opengis.net/citygml/appearance/2.0"),
    ("tran", "http://www.opengis.net/citygml/transportation/2.0"),
    ("veg", "http://www.opengis.net/citygml/vegetation/2.0"),
    ("dem", "http://www.opengis.net/citygml/relief/2.0"),
    ("gml", "http://www.opengis.net/gml"),
    ("xlink", "http://www.w3.org/1999/xlink"),
    ("xAL", "urn:oasis:names:tc:ciq:xsdschema:xAL:2.0"),
    ("xsi", "http://www.w3.org/2001/XMLSchema-instance"),
)
CITYGML_SCHEMA_LOCATION = ("http://www.opengis.net/citygml/building/2.0 "
                           "http://schemas.opengis.net/citygml/building/2.0/building.xsd "
                           "http://www.opengis.net/citygml/generics/2.0 "
                           "http://schemas.opengis.net/citygml/generics/2.0/generics.xsd")
ENERGY_ADE_NS = "http://www.sig3d.org/citygml/2.0/energy/1.0"
CITYGML_SRS = "urn:ogc:def:crs,crs:EPSG::25833,crs:EPSG::5783"
CITYGML_ORIGIN = (385_000.0, 5_818_000.0)   # Berlin, ETRS89 / UTM 33N
CITYGML_EXTENT = 12_000.0                   # metres either way
CITYGML_STREETS = (
    "Unter den Linden", "Straße des 17. Juni", "Müllerstraße", "Karl-Marx-Allee",
    "Ulica Świętokrzyska", "Οδός Ερμού", "Улица Арбат", "شارع الرشيد", "東京通り", "Rue de l'Église",
)
CITYGML_OWNERS = (
    "Müller &amp; Söhne GmbH", "Bezirksamt Mitte", "Wohnungsbau &#x2014; Genossenschaft",
    "Ærø Holdings", "Zoë &amp; Chloé SARL", "&lt;unknown&gt;",
)
CITYGML_ROOF_COLOURS = ("#8B4513", "#A52A2A", "#2F4F4F", "#708090", "#B22222")


def _cg_poslist(points):
    return " ".join(f"{x:.3f} {y:.3f} {z:.3f}" for x, y, z in points)


def _cg_polygon(rng, pid, points, pad):
    ring = list(points) + [points[0]]
    return (f'{pad}<gml:Polygon gml:id="{pid}">\n'
            f'{pad}  <gml:exterior>\n'
            f'{pad}    <gml:LinearRing gml:id="{pid}_ring">\n'
            f'{pad}      <gml:posList srsDimension="3">{_cg_poslist(ring)}</gml:posList>\n'
            f'{pad}    </gml:LinearRing>\n'
            f'{pad}  </gml:exterior>\n'
            f'{pad}</gml:Polygon>\n')


def _cg_surface(rng, kind, sid, points, pad, p):
    """A thematic boundary surface - WallSurface, RoofSurface, GroundSurface - holding one
    LOD2 polygon. `p` is the element prefix for the bldg namespace ('bldg:' or '' under a
    default namespace)."""
    return (f'{pad}<{p}boundedBy>\n'
            f'{pad}  <{p}{kind} gml:id="{sid}">\n'
            f'{pad}    <{p}lod2MultiSurface>\n'
            f'{pad}      <gml:MultiSurface srsName="{CITYGML_SRS}" srsDimension="3">\n'
            f'{pad}        <gml:surfaceMember>\n'
            + _cg_polygon(rng, f"{sid}_poly", points, pad + "          ") +
            f'{pad}        </gml:surfaceMember>\n'
            f'{pad}      </gml:MultiSurface>\n'
            f'{pad}    </{p}lod2MultiSurface>\n'
            f'{pad}  </{p}{kind}>\n'
            f'{pad}</{p}boundedBy>\n')


def _cg_shell(rng, bid, pad, p):
    """A gabled box: footprint, four walls, two roof planes, and the solid that references them."""
    x0 = CITYGML_ORIGIN[0] + rng.uniform(-CITYGML_EXTENT, CITYGML_EXTENT)
    y0 = CITYGML_ORIGIN[1] + rng.uniform(-CITYGML_EXTENT, CITYGML_EXTENT)
    w, d = rng.uniform(8, 40), rng.uniform(8, 30)
    z0 = rng.uniform(30, 60)
    eaves = z0 + rng.uniform(6, 30)
    ridge = eaves + rng.uniform(2, 8)
    a, b, c, e = (x0, y0), (x0 + w, y0), (x0 + w, y0 + d), (x0, y0 + d)
    mid0, mid1 = (x0, y0 + d / 2), (x0 + w, y0 + d / 2)

    surfaces = [
        ("GroundSurface", [(*a, z0), (*e, z0), (*c, z0), (*b, z0)]),
        ("WallSurface", [(*a, z0), (*b, z0), (*b, eaves), (*a, eaves)]),
        ("WallSurface", [(*b, z0), (*c, z0), (*c, eaves), (*mid1, ridge), (*b, eaves)]),
        ("WallSurface", [(*c, z0), (*e, z0), (*e, eaves), (*c, eaves)]),
        ("WallSurface", [(*e, z0), (*a, z0), (*a, eaves), (*mid0, ridge), (*e, eaves)]),
        ("RoofSurface", [(*a, eaves), (*b, eaves), (*mid1, ridge), (*mid0, ridge)]),
        ("RoofSurface", [(*mid0, ridge), (*mid1, ridge), (*c, eaves), (*e, eaves)]),
    ]
    ids = [f"{bid}_{kind[0].lower()}{n}" for n, (kind, _) in enumerate(surfaces)]

    solid = (f'{pad}<{p}lod2Solid>\n'
             f'{pad}  <gml:Solid gml:id="{bid}_solid">\n'
             f'{pad}    <gml:exterior>\n'
             f'{pad}      <gml:CompositeSurface>\n'
             + "".join(f'{pad}        <gml:surfaceMember xlink:href="#{sid}_poly"/>\n' for sid in ids) +
             f'{pad}      </gml:CompositeSurface>\n'
             f'{pad}    </gml:exterior>\n'
             f'{pad}  </gml:Solid>\n'
             f'{pad}</{p}lod2Solid>\n')
    bounded = "".join(_cg_surface(rng, kind, sid, pts, pad, p) for sid, (kind, pts) in zip(ids, surfaces))
    return solid + bounded, ids, ridge - z0


def _cg_address(rng, pad, p):
    street = rng.choice(CITYGML_STREETS)
    return (f'{pad}<{p}address>\n'
            f'{pad}  <core:Address>\n'
            f'{pad}    <core:xalAddress>\n'
            f'{pad}      <xAL:AddressDetails>\n'
            f'{pad}        <xAL:Country>\n'
            f'{pad}          <xAL:CountryName>Germany</xAL:CountryName>\n'
            f'{pad}          <xAL:Locality Type="Town">\n'
            f'{pad}            <xAL:LocalityName>Berlin</xAL:LocalityName>\n'
            f'{pad}            <xAL:Thoroughfare Type="Street">\n'
            f'{pad}              <xAL:ThoroughfareNumber>{rng.randint(1, 240)}</xAL:ThoroughfareNumber>\n'
            f'{pad}              <xAL:ThoroughfareName>{street}</xAL:ThoroughfareName>\n'
            f'{pad}            </xAL:Thoroughfare>\n'
            f'{pad}            <xAL:PostalCode>\n'
            f'{pad}              <xAL:PostalCodeNumber>{rng.randint(10115, 14199)}</xAL:PostalCodeNumber>\n'
            f'{pad}            </xAL:PostalCode>\n'
            f'{pad}          </xAL:Locality>\n'
            f'{pad}        </xAL:Country>\n'
            f'{pad}      </xAL:AddressDetails>\n'
            f'{pad}    </core:xalAddress>\n'
            f'{pad}  </core:Address>\n'
            f'{pad}</{p}address>\n')


def _cg_generics(rng, i, pad):
    surveyed = datetime.date(2015, 1, 1) + datetime.timedelta(days=rng.randint(0, 3650))
    return (f'{pad}<gen:stringAttribute name="owner">\n'
            f'{pad}  <gen:value>{rng.choice(CITYGML_OWNERS)}</gen:value>\n'
            f'{pad}</gen:stringAttribute>\n'
            f'{pad}<gen:stringAttribute name="roofColour">\n'
            f'{pad}  <gen:value>{rng.choice(CITYGML_ROOF_COLOURS)}</gen:value>\n'
            f'{pad}</gen:stringAttribute>\n'
            f'{pad}<gen:doubleAttribute name="footprintArea">\n'
            f'{pad}  <gen:value>{rng.uniform(40, 1200):.2f}</gen:value>\n'
            f'{pad}</gen:doubleAttribute>\n'
            f'{pad}<gen:intAttribute name="cadastralParcel">\n'
            f'{pad}  <gen:value>{100000 + i}</gen:value>\n'
            f'{pad}</gen:intAttribute>\n'
            f'{pad}<gen:dateAttribute name="surveyed">\n'
            f'{pad}  <gen:value>{surveyed.isoformat()}</gen:value>\n'
            f'{pad}</gen:dateAttribute>\n'
            f'{pad}<gen:uriAttribute name="registry">\n'
            f'{pad}  <gen:value>https://registry.example.org/parcels/{100000 + i}?view=full&amp;lang=de</gen:value>\n'
            f'{pad}</gen:uriAttribute>\n')


def _cg_building(rng, i, pad, *, default_ns=False, energy_ade=False, parts=0, part_number=0):
    """One bldg:Building with `parts` bldg:BuildingParts inside it, or - given a `part_number` -
    one of those parts. `default_ns` writes it under a default namespace declaration with
    unprefixed bldg elements: the same element, spelled the other way XML allows. Its parts keep
    the bldg prefix, which the root still binds. `energy_ade` adds Energy ADE properties with
    the prefix declared on the building."""
    element = "BuildingPart" if part_number else "Building"
    bid = f"BLDG_{i:07d}_part{part_number}" if part_number else f"BLDG_{i:07d}"
    p = "" if default_ns else "bldg:"
    decls = f' xmlns="{CITYGML_NAMESPACES[1][1]}"' if default_ns else ""
    if energy_ade:
        decls += f' xmlns:energy="{ENERGY_ADE_NS}"'
    created = datetime.datetime(2020, 1, 1) + datetime.timedelta(seconds=rng.randint(0, 5 * 365 * 86400))

    out = [f'{pad}<{p}{element} gml:id="{bid}"{decls}>\n']
    inner = pad + "  "
    if i % 13 == 0:
        out.append(f'{inner}<gml:description><![CDATA[Survey note: wall <north> & roof replaced; '
                   f'see "plan 4" <b>before</b> 2030.]]></gml:description>\n')
    elif i % 17 == 0:
        out.append(f'{inner}<gml:description>Listed building &#x2014; façade &lt;protected&gt; since 1987 &amp; '
                   f'interior since 2004</gml:description>\n')
    out.append(f'{inner}<gml:name>Gebäude {i}</gml:name>\n')
    out.append(f'{inner}<core:creationDate>{created.date().isoformat()}</core:creationDate>\n')
    out.append(f'{inner}<core:externalReference>\n'
               f'{inner}  <core:informationSystem>https://www.berlin.de/sen/sbw/stadtdaten/</core:informationSystem>\n'
               f'{inner}  <core:externalObject>\n'
               f'{inner}    <core:name>DEBE{i:011d}</core:name>\n'
               f'{inner}  </core:externalObject>\n'
               f'{inner}</core:externalReference>\n')
    out.append(_cg_generics(rng, i, inner))
    out.append(f'{inner}<{p}class codeSpace="http://www.sig3d.org/codelists/citygml/2.0/building/2.0/_AbstractBuilding_class.xml">'
               f'{rng.choice((1000, 1010, 1020, 1060))}</{p}class>\n')
    out.append(f'{inner}<{p}function codeSpace="http://www.sig3d.org/codelists/citygml/2.0/building/2.0/_AbstractBuilding_function.xml">'
               f'31001_{rng.choice((1000, 1010, 2000, 3020))}</{p}function>\n')
    out.append(f'{inner}<{p}yearOfConstruction>{rng.randint(1850, 2024)}</{p}yearOfConstruction>\n')
    out.append(f'{inner}<{p}roofType codeSpace="http://www.sig3d.org/codelists/citygml/2.0/building/2.0/_AbstractBuilding_roofType.xml">1030</{p}roofType>\n')
    shell, ids, height = _cg_shell(rng, bid, inner, p)
    out.append(f'{inner}<{p}measuredHeight uom="urn:adv:uom:m">{height:.2f}</{p}measuredHeight>\n')
    out.append(f'{inner}<{p}storeysAboveGround>{max(1, int(height // 3))}</{p}storeysAboveGround>\n')
    out.append(f'{inner}<{p}storeyHeightsAboveGround uom="urn:adv:uom:m">3.0 3.0 2.8</{p}storeyHeightsAboveGround>\n')
    if energy_ade:
        out.append(f'{inner}<energy:volume>\n'
                   f'{inner}  <energy:VolumeType>\n'
                   f'{inner}    <energy:type>grossVolume</energy:type>\n'
                   f'{inner}    <energy:value uom="m3">{rng.uniform(300, 9000):.1f}</energy:value>\n'
                   f'{inner}  </energy:VolumeType>\n'
                   f'{inner}</energy:volume>\n'
                   f'{inner}<energy:referencePoint>\n'
                   f'{inner}  <gml:Point srsName="{CITYGML_SRS}" srsDimension="3">\n'
                   f'{inner}    <gml:pos>{CITYGML_ORIGIN[0]:.3f} {CITYGML_ORIGIN[1]:.3f} 34.000</gml:pos>\n'
                   f'{inner}  </gml:Point>\n'
                   f'{inner}</energy:referencePoint>\n')
    out.append(shell)
    for n in range(parts):
        out.append(f'{inner}<{p}consistsOfBuildingPart>\n')
        out.append(_cg_building(rng, i, inner + "  ", part_number=n + 1)[0])
        out.append(f'{inner}</{p}consistsOfBuildingPart>\n')
    if not part_number:
        out.append(_cg_address(rng, inner, p))
    out.append(f'{pad}</{p}{element}>\n')
    return "".join(out), ids


def _cg_vegetation(rng, i, pad):
    x = CITYGML_ORIGIN[0] + rng.uniform(-CITYGML_EXTENT, CITYGML_EXTENT)
    y = CITYGML_ORIGIN[1] + rng.uniform(-CITYGML_EXTENT, CITYGML_EXTENT)
    return (f'{pad}<veg:SolitaryVegetationObject gml:id="VEG_{i:07d}">\n'
            f'{pad}  <gml:name>Tilia cordata {i}</gml:name>\n'
            f'{pad}  <veg:species codeSpace="http://www.sig3d.org/codelists/citygml/2.0/vegetation/2.0/SolitaryVegetationObject_species.xml">1070</veg:species>\n'
            f'{pad}  <veg:height uom="urn:adv:uom:m">{rng.uniform(4, 28):.1f}</veg:height>\n'
            f'{pad}  <veg:trunkDiameter uom="urn:adv:uom:m">{rng.uniform(0.2, 1.4):.2f}</veg:trunkDiameter>\n'
            f'{pad}  <veg:crownDiameter uom="urn:adv:uom:m">{rng.uniform(2, 14):.1f}</veg:crownDiameter>\n'
            f'{pad}  <veg:lod1ImplicitRepresentation>\n'
            f'{pad}    <core:ImplicitGeometry>\n'
            f'{pad}      <core:mimeType>model/obj</core:mimeType>\n'
            f'{pad}      <core:libraryObject>https://models.example.org/trees/tilia.obj</core:libraryObject>\n'
            f'{pad}      <core:referencePoint>\n'
            f'{pad}        <gml:Point srsName="{CITYGML_SRS}" srsDimension="3">\n'
            f'{pad}          <gml:pos>{x:.3f} {y:.3f} {rng.uniform(30, 60):.3f}</gml:pos>\n'
            f'{pad}        </gml:Point>\n'
            f'{pad}      </core:referencePoint>\n'
            f'{pad}    </core:ImplicitGeometry>\n'
            f'{pad}  </veg:lod1ImplicitRepresentation>\n'
            f'{pad}</veg:SolitaryVegetationObject>\n')


def _cg_road(rng, i, pad):
    x = CITYGML_ORIGIN[0] + rng.uniform(-CITYGML_EXTENT, CITYGML_EXTENT)
    y = CITYGML_ORIGIN[1] + rng.uniform(-CITYGML_EXTENT, CITYGML_EXTENT)
    length, width, z = rng.uniform(40, 300), rng.uniform(6, 20), rng.uniform(30, 60)
    pts = [(x, y, z), (x + length, y, z), (x + length, y + width, z), (x, y + width, z)]
    return (f'{pad}<tran:Road gml:id="ROAD_{i:07d}">\n'
            f'{pad}  <gml:name>{rng.choice(CITYGML_STREETS)}</gml:name>\n'
            f'{pad}  <tran:function codeSpace="http://www.sig3d.org/codelists/citygml/2.0/transportation/2.0/Road_function.xml">1000</tran:function>\n'
            f'{pad}  <tran:surfaceMaterial>asphalt</tran:surfaceMaterial>\n'
            f'{pad}  <tran:lod1MultiSurface>\n'
            f'{pad}    <gml:MultiSurface srsName="{CITYGML_SRS}" srsDimension="3">\n'
            f'{pad}      <gml:surfaceMember>\n'
            + _cg_polygon(rng, f"ROAD_{i:07d}_poly", pts, pad + "        ") +
            f'{pad}      </gml:surfaceMember>\n'
            f'{pad}    </gml:MultiSurface>\n'
            f'{pad}  </tran:lod1MultiSurface>\n'
            f'{pad}</tran:Road>\n')


def _cg_relief(rng, pad, points):
    """A breakline relief whose one posList is tens of MB - the XML counterpart of the valid
    sample's 10MB string, for a text node far past the display cap."""
    x, y, z = CITYGML_ORIGIN[0] - CITYGML_EXTENT, CITYGML_ORIGIN[1], 40.0
    coords = []
    for _ in range(points):
        x += rng.uniform(0.5, 2.0)
        y += rng.uniform(-1.0, 1.0)
        z += rng.uniform(-0.05, 0.05)
        coords.append(f"{x:.3f} {y:.3f} {z:.3f}")
    return (f'{pad}<dem:ReliefFeature gml:id="DEM_breaklines">\n'
            f'{pad}  <gml:name>Spree embankment breaklines</gml:name>\n'
            f'{pad}  <dem:lod>2</dem:lod>\n'
            f'{pad}  <dem:reliefComponent>\n'
            f'{pad}    <dem:BreaklineRelief gml:id="DEM_breaklines_1">\n'
            f'{pad}      <dem:lod>2</dem:lod>\n'
            f'{pad}      <dem:ridgeOrValleyLines>\n'
            f'{pad}        <gml:MultiCurve srsName="{CITYGML_SRS}" srsDimension="3">\n'
            f'{pad}          <gml:curveMember>\n'
            f'{pad}            <gml:LineString gml:id="DEM_breaklines_1_line">\n'
            f'{pad}              <gml:posList srsDimension="3" count="{points}">' + " ".join(coords) + '</gml:posList>\n'
            f'{pad}            </gml:LineString>\n'
            f'{pad}          </gml:curveMember>\n'
            f'{pad}        </gml:MultiCurve>\n'
            f'{pad}      </dem:ridgeOrValleyLines>\n'
            f'{pad}    </dem:BreaklineRelief>\n'
            f'{pad}  </dem:reliefComponent>\n'
            f'{pad}</dem:ReliefFeature>\n')


def _cg_appearance(rng, n, targets, pad):
    """One appearance theme: a material on some surfaces and a texture on others."""
    material_targets = "".join(f'{pad}        <app:target>#{t}_poly</app:target>\n' for t in targets[::2])
    texture_targets = "".join(
        f'{pad}        <app:target uri="#{t}_poly">\n'
        f'{pad}          <app:TexCoordList>\n'
        f'{pad}            <app:textureCoordinates ring="#{t}_poly_ring">0.0 0.0 1.0 0.0 1.0 1.0 0.0 1.0 0.0 0.0</app:textureCoordinates>\n'
        f'{pad}          </app:TexCoordList>\n'
        f'{pad}        </app:target>\n' for t in targets[1::2])
    return (f'{pad}<app:appearanceMember>\n'
            f'{pad}  <app:Appearance gml:id="APP_{n:05d}">\n'
            f'{pad}    <app:theme>{"summer" if n % 2 else "winter"}</app:theme>\n'
            f'{pad}    <app:surfaceDataMember>\n'
            f'{pad}      <app:X3DMaterial gml:id="APP_{n:05d}_mat">\n'
            f'{pad}        <app:diffuseColor>{rng.random():.3f} {rng.random():.3f} {rng.random():.3f}</app:diffuseColor>\n'
            f'{pad}        <app:transparency>0.0</app:transparency>\n'
            f'{pad}        <app:isSmooth>false</app:isSmooth>\n'
            + material_targets +
            f'{pad}      </app:X3DMaterial>\n'
            f'{pad}    </app:surfaceDataMember>\n'
            f'{pad}    <app:surfaceDataMember>\n'
            f'{pad}      <app:ParameterizedTexture gml:id="APP_{n:05d}_tex">\n'
            f'{pad}        <app:imageURI>https://textures.example.org/facades/{n:05d}.jpg</app:imageURI>\n'
            f'{pad}        <app:mimeType>image/jpeg</app:mimeType>\n'
            f'{pad}        <app:wrapMode>wrap</app:wrapMode>\n'
            + texture_targets +
            f'{pad}      </app:ParameterizedTexture>\n'
            f'{pad}    </app:surfaceDataMember>\n'
            f'{pad}  </app:Appearance>\n'
            f'{pad}</app:appearanceMember>\n')


def make_citygml_sample():
    """A CityGML 2.0 city model of about 500MB, for the XML view. Its own seeded Random, so it
    never shifts the other files' shared stream.

    One root (core:CityModel) holding ~70,000 core:cityObjectMember children - a wide container -
    each a building (or a tree or a road) nested about 15 deep. Eleven namespaces declared on the
    root and attributes on most elements. Also, by design, every construct a tree reader of XML
    has to cope with: an XML declaration and a stylesheet processing instruction before the root,
    comments between members, a processing instruction inside an element, CDATA holding '<', '&'
    and quotes, the predefined entities and numeric character references in text and attribute
    values, raw UTF-8 street names in five scripts, a namespace declared below the root (the
    Energy ADE), the same element spelled under a default namespace, self-closing elements, and
    one text node of tens of MB. Mixed content and a DOCTYPE are not CityGML, so they are left
    to unit tests.
    """
    start = time.time()
    rng = random.Random(2027)
    pad = "  "
    members = 0
    written = 0
    notes = {}
    appearance_targets = []

    with open(XML_OUT, "w", encoding="utf-8", newline="\n", buffering=4 * 1024 * 1024) as f:
        def put(text):
            nonlocal written
            f.write(text)
            written += len(text)

        put('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n')
        put('<?xml-stylesheet type="text/xsl" href="citygml-overview.xsl"?>\n')
        # No "--" inside a comment: XML forbids it, so the option is not named here.
        put('<!-- Generated by make-test-json.py: a synthetic CityGML 2.0 model of Berlin.\n'
            '     Coordinates are ETRS89 / UTM zone 33N with DHHN2016 heights. -->\n')
        decls = "".join(f'\n    xmlns:{prefix}="{uri}"' for prefix, uri in CITYGML_NAMESPACES)
        put(f'<core:CityModel{decls}\n    xsi:schemaLocation="{CITYGML_SCHEMA_LOCATION}">\n')
        put(f'{pad}<gml:name>Argonaut test city</gml:name>\n')
        lo = (CITYGML_ORIGIN[0] - CITYGML_EXTENT, CITYGML_ORIGIN[1] - CITYGML_EXTENT)
        hi = (CITYGML_ORIGIN[0] + CITYGML_EXTENT, CITYGML_ORIGIN[1] + CITYGML_EXTENT)
        put(f'{pad}<gml:boundedBy>\n'
            f'{pad}  <gml:Envelope srsName="{CITYGML_SRS}" srsDimension="3">\n'
            f'{pad}    <gml:lowerCorner>{lo[0]:.3f} {lo[1]:.3f} 20.000</gml:lowerCorner>\n'
            f'{pad}    <gml:upperCorner>{hi[0]:.3f} {hi[1]:.3f} 150.000</gml:upperCorner>\n'
            f'{pad}  </gml:Envelope>\n'
            f'{pad}</gml:boundedBy>\n')

        i = 0
        relief_written = False
        while written < XML_TARGET:
            if i % 500 == 0:
                put(f'{pad}<!-- district {i // 500 + 1} -->\n')
            if i % 1000 == 999:
                put(f'{pad}<?argonaut-checkpoint member="{members + 1}"?>\n')

            if not relief_written and written >= XML_TARGET // 3:
                body = _cg_relief(rng, pad + "  ", XML_LONG_POSLIST_POINTS)
                relief_written = True
                notes["long posList"] = (members + 1, "dem:ReliefFeature/dem:reliefComponent/dem:BreaklineRelief/"
                                         "dem:ridgeOrValleyLines/gml:MultiCurve/gml:curveMember/gml:LineString/gml:posList")
            elif i % 11 == 5:
                body = _cg_road(rng, i, pad + "  ")
            elif i % 7 == 3:
                body = _cg_vegetation(rng, i, pad + "  ")
            else:
                # Offset from one another, so no member carries every special case at once.
                default_ns = i % 97 == 48
                energy = i % 53 == 26
                parts = 2 if i % 250 == 124 else 0
                body, ids = _cg_building(rng, i, pad + "  ", default_ns=default_ns, energy_ade=energy, parts=parts)
                if i % 40 == 0:
                    appearance_targets.extend(ids[:4])
                if default_ns and "default namespace" not in notes:
                    notes["default namespace"] = (members + 1, "Building")
                if energy and "Energy ADE" not in notes:
                    notes["Energy ADE"] = (members + 1, "bldg:Building/energy:volume")
                if parts and "building parts" not in notes:
                    notes["building parts"] = (members + 1, "bldg:Building/bldg:consistsOfBuildingPart[2]/bldg:BuildingPart")

            put(f'{pad}<core:cityObjectMember>\n{body}{pad}</core:cityObjectMember>\n')
            members += 1
            i += 1

        notes["last member"] = (members, "")
        for n in range(0, len(appearance_targets), 8):
            put(_cg_appearance(rng, n // 8, appearance_targets[n:n + 8], pad))
        put('</core:CityModel>\n')
        put('<!-- end of model -->\n')

    size = os.path.getsize(XML_OUT)
    print(f"DONE: CityGML sample ~{size / 1024**2:.1f} MiB, {members:,} city objects, "
          f"{(len(appearance_targets) + 7) // 8:,} appearances -> {XML_OUT}  ({time.time() - start:.1f}s)")
    print("  XPaths worth typing into the path bar:")
    for label, (member, rest) in notes.items():
        path = f"/core:CityModel/core:cityObjectMember[{member}]" + (f"/{rest}" if rest else "")
        print(f"    {path}   {label}", flush=True)


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
    parser.add_argument("--yaml", action="store_true", help="generate only the ~1MB YAML config file")
    parser.add_argument("--xml", action="store_true",
                        help="generate the ~500MB CityGML XML file (never part of a bare run)")
    parser.add_argument("--huge", action="store_true",
                        help="generate the 100GB NDJSON stress file (never part of a bare run)")
    parser.add_argument("--huge-gb", type=float, default=HUGE_DEFAULT_GB, metavar="GB",
                        help=f"size of the --huge file in GiB (default {HUGE_DEFAULT_GB})")
    args = parser.parse_args()

    if args.huge:
        os.makedirs(OUT_DIR, exist_ok=True)
        make_huge_ndjson(args.huge_gb)
        if not (args.corrupt or args.valid or args.geojson or args.wide or args.ini or args.yaml or args.xml):
            return

    if args.xml:
        os.makedirs(OUT_DIR, exist_ok=True)
        make_citygml_sample()
        if not (args.corrupt or args.valid or args.geojson or args.wide or args.ini or args.yaml):
            return

    everything = not (args.corrupt or args.valid or args.geojson or args.wide or args.ini or args.yaml)

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
    if args.yaml or everything:
        make_yaml_sample()


if __name__ == "__main__":
    main()
