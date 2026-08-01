#!/usr/bin/env python3
"""Generates test JSON fixtures for the Argonaut JSON viewer:

  - corrupt-sample-4gb.json: ~4GB, valid up to ~3GB in, then structurally invalid - for
    testing JsonStructureIndex's partial-index / IndexFailure path (a scan that stops with
    items already published).
  - valid-sample-50mb.json: ~50MB, top-level array of 15 well-formed objects, each with
    string/number/bool/array-of-string/array-of-number/array-of-object properties, a
    JS-epoch-ms "date" property, and a 4-object-deep nested hierarchy - for baseline
    valid-file rendering. Object[1] (the second object) carries a single ~10MB string
    property to exercise overflow rendering for a pathologically long value.

Run with no arguments to generate both. Pass --corrupt or --valid to generate just one -
the 4GB corrupt file takes minutes, so --valid alone is handy for quick UI iteration.
"""
import argparse, os, random, time

OUT_DIR = os.path.expanduser("~/testData")
CORRUPT_OUT = os.path.join(OUT_DIR, "corrupt-sample-4gb.json")
VALID_OUT = os.path.join(OUT_DIR, "valid-sample-50mb.json")

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


def render_object(i, fields, extra_key, extra_val):
    name, value, active, date_ms, tags, numbers, objects, nested = fields
    return (
        f'{{"id":{i},"name":"{name}","value":{value},"active":{active},"date":{date_ms},'
        f'"tags":[{tags}],"numbers":[{numbers}],"objects":[{objects}],'
        f'"nested":{nested},'
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
    with open(VALID_OUT, "w", buffering=1024 * 1024) as f:
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


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--corrupt", action="store_true", help="generate only the 4GB corrupt file")
    parser.add_argument("--valid", action="store_true", help="generate only the 50MB valid file")
    args = parser.parse_args()

    both = not (args.corrupt or args.valid)

    os.makedirs(OUT_DIR, exist_ok=True)
    if args.valid or both:
        make_valid_sample()
    if args.corrupt or both:
        make_corrupt_json()


if __name__ == "__main__":
    main()
