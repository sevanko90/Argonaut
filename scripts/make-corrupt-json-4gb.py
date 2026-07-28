#!/usr/bin/env python3
"""Generates a ~4GB JSON file for the Argonaut JSON viewer that is valid up to ~3GB in,
then becomes structurally invalid - for testing JsonStructureIndex's partial-index /
IndexFailure path (a scan that stops with items already published).

Layout:
  - top-level array of small objects: {"id": N, "name": "...", "value": ..., "active": ...,
    "tags": [...], "nested": {...}}
  - valid, parseable JSON from byte 0 up to ~3GiB (CORRUPT_AT)
  - at CORRUPT_AT: a lone '#' is injected where the next array element would start - not
    valid JSON syntax anywhere, so Utf8JsonReader throws immediately on it
  - after the corruption point, more object-shaped (but never parsed, since the reader
    already stopped) filler continues to pad the file out to ~4GiB total
  - the array is never closed - once a reader has faulted, nothing after the fault matters
"""
import os, random, time

OUT = os.path.expanduser("~/testData/corrupt-sample-4gb.json")
TARGET = 4 * 1024**3
CORRUPT_AT = 3 * 1024**3
CHECKPOINT_EVERY = 250 * 1024**2

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


def main():
    start = time.time()
    written = 0
    i = 0
    next_checkpoint = CHECKPOINT_EVERY
    corrupted = False

    with open(OUT, "wb", buffering=1024 * 1024) as f:
        def put(data: bytes):
            nonlocal written
            f.write(data)
            written += len(data)

        put(b"[\n")

        while written < TARGET - 64:
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

    print(f"DONE: {written:,} bytes in {time.time() - start:.0f}s -> {OUT}", flush=True)
    print(f"Corruption injected at byte offset {CORRUPT_AT:,} (~3GiB)", flush=True)


if __name__ == "__main__":
    main()
