#!/usr/bin/env python3
"""Generates ~4GB of mixed raw test data for the Argonaut raw viewer.

Layout (tags are grep-able, all wrapped in ### ... ###):
  - line 1..2: comma-free prose header (so FileTypeDetector yields Unidentified)
  - ###TAG_START### near the top
  - normal lines 60-200 chars, with ###TAG_CHECKPOINT_NNN### every ~100MB
  - at ~1GB:   one ~100MB line: ###TAG_BIGLINE_A_START### ... ###TAG_BIGLINE_A_MID### ... ###TAG_BIGLINE_A_END###
  - at ~2GB:   ###TAG_BINARY### then ~10MB of random binary bytes (invalid UTF-8, control chars)
  - at ~3GB:   the second ~100MB line (BIGLINE_B, same start/mid/end tags)
  - last line: ###TAG_END###
"""
import os, random, sys, time

OUT = os.path.expanduser("~/testData/raw-sample-4gb.dat")
TARGET = 4 * 1024**3
CHECKPOINT_EVERY = 100 * 1024**2
BIGLINE_SIZE = 100 * 1024**2
BINARY_SIZE = 10 * 1024**2

random.seed(42)
WORDS = ("alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima "
         "mike november oscar papa quebec romeo sierra tango uniform victor whiskey "
         "xray yankee zulu lorem ipsum dolor sit amet consectetur adipiscing elit "
         "payload sensor telemetry packet frame buffer offset segment index marker").split()

def normal_batch(first_line_no, n_lines):
    """n_lines pseudo-random prose lines, 60-200 chars each, numbered."""
    lines = []
    for i in range(n_lines):
        target_len = random.randint(60, 200)
        parts = [f"L{first_line_no + i:09d}"]
        length = len(parts[0])
        while length < target_len:
            w = random.choice(WORDS)
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

def main():
    start = time.time()
    written = 0
    line_no = 1
    checkpoint = 1
    next_checkpoint = CHECKPOINT_EVERY
    biglines_at = {1 * 1024**3: "A", 3 * 1024**3: "B"}
    binary_at = 2 * 1024**3
    binary_done = False

    with open(OUT, "wb", buffering=1024*1024) as f:
        def put(data):
            nonlocal written
            f.write(data)
            written += len(data)

        put(b"RAW SAMPLE FILE for the Argonaut raw viewer - unstructured prose header\n")
        put(b"second header line without structure so detection stays unidentified\n")
        put(b"###TAG_START###\n")

        while written < TARGET - 64:
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

            batch = normal_batch(line_no, 50_000)
            line_no += 50_000
            put(batch)
            if line_no % 1_000_000 < 50_000:
                print(f"{written / 1024**3:.2f} GiB written ({time.time() - start:.0f}s)", flush=True)

        put(b"###TAG_END###\n")

    print(f"DONE: {written:,} bytes in {time.time() - start:.0f}s -> {OUT}", flush=True)

if __name__ == "__main__":
    main()
