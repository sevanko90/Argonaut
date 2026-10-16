#!/usr/bin/env python3
"""Generates filter files for `--lines --filter` and `--bytes --filter`, aimed at the samples the
other make-*.py scripts write - so run those first. Each filter names real places in its sample,
found by scanning it, so the selection opens on something worth looking at rather than on
arbitrary numbers.

For sample-1gb.csv (make-csv-sample-1gb.py):
  - filter-csv-lines.txt: every entry form a lines filter takes (N, A-B, A+COUNT, A-, k/m),
    overlaps, touching entries and duplicates to merge, comments and blank lines, the header,
    the file's last lines, one entry the file's end clips and two it drops entirely.
  - filter-csv-lines-1m.txt: 1,000,000 single lines in random order with duplicates - for timing
    the parse, the merge and the locating pass on a filter a third-party app might really write.

For raw-sample-4gb.dat (make-raw-sample.py):
  - filter-raw-lines.txt: the line of every ###...### tag with a line of context either side -
    the catalogues, each checkpoint, both ~100MB lines and the binary block.
  - filter-raw-bytes.txt: byte ranges around the same tags - mid-way into each ~100MB line, into
    the binary block, written in hex and with a g suffix, plus one that starts inside a UTF-8
    character, which the view must snap back to the character's start.

Not tied to a sample:
  - filter-empty.txt: comments and blank lines only - the view refuses to open and says why.
  - filter-invalid.txt: an unreadable entry on line 4 - the error names that line.

Edit one while it is open to watch the view follow it.

Usage:
  make-filter-samples.py                     # write into ~/testData
  make-filter-samples.py --dir ~/elsewhere   # where the samples are, and where filters go
"""
import argparse, mmap, os, random, re, time

DEFAULT_DIR = os.path.expanduser("~/testData")
CSV_SAMPLE = "sample-1gb.csv"
RAW_SAMPLE = "raw-sample-4gb.dat"
CHUNK = 64 * 1024**2
TAG = re.compile(rb"###([A-Za-z0-9_]+)###")
MANY_ENTRIES = 1_000_000

random.seed(42)


def count_lines(path):
    """Lines in the file, counting a last line that has no newline of its own."""
    lines, last = 0, b"\n"
    with open(path, "rb") as f:
        while chunk := f.read(CHUNK):
            lines += chunk.count(b"\n")
            last = chunk[-1:]
    return lines + (last != b"\n")


def find_tags(path):
    """(byte offset, 1-based line, name) of every ###NAME### tag, in one forward pass."""
    tags = []
    with open(path, "rb") as f, mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as m:
        size, start, line = len(m), 0, 1
        while start < size:
            # Overlap the next chunk by a tag's length so one straddling the boundary is found.
            end = min(size, start + CHUNK)
            window = m[start:min(size, end + 128)]
            counted = 0
            for match in TAG.finditer(window):
                if match.start() >= end - start:
                    break
                line += window.count(b"\n", counted, match.start())
                counted = match.start()
                tags.append((start + match.start(), line, match.group(1).decode()))
            line += window.count(b"\n", counted, end - start)
            start = end
    return tags


def write(path, text):
    with open(path, "w", newline="\n") as f:
        f.write(text)
    print(f"  {os.path.basename(path)} ({os.path.getsize(path):,} bytes)")


def csv_filters(out_dir):
    sample = os.path.join(out_dir, CSV_SAMPLE)
    if not os.path.exists(sample):
        print(f"SKIP: {CSV_SAMPLE} not found - run make-csv-sample-1gb.py first")
        return
    n = count_lines(sample)
    print(f"{CSV_SAMPLE}: {n:,} lines")
    write(os.path.join(out_dir, "filter-csv-lines.txt"), f"""\
# Lines of {CSV_SAMPLE} ({n:,} lines when written). Open with:
#   Argonaut {CSV_SAMPLE} --lines --filter filter-csv-lines.txt
1                   # the header
5000, 40-60         # written out of order, two to a line
2-50                # overlaps 40-60: merges into 2-60
61-70               # touches it: merges into 2-70
5000                # a duplicate

1k                  # 1,000
10k+5               # 10,000-10,004
1.5m+100            # k and m suffixes with a count
2m-2000010
{n - 9}-               # to the end: the file's last 10 lines
{n - 2}-{n + 50}        # clipped at the end of the file
{n + 1000}+20          # past the end: dropped, and a toast counts it
{n + 5000}             # and this one
""")

    entries = [str(random.randint(1, n)) for _ in range(MANY_ENTRIES)]
    for i in range(0, MANY_ENTRIES, 1000):
        entries[i] = entries[i + 1]  # a duplicate in every thousand
    write(os.path.join(out_dir, "filter-csv-lines-1m.txt"),
          f"# {MANY_ENTRIES:,} random lines of {CSV_SAMPLE}, unsorted, some duplicated. Open with:\n"
          f"#   Argonaut {CSV_SAMPLE} --lines --filter filter-csv-lines-1m.txt\n"
          + "\n".join(entries) + "\n")


def raw_filters(out_dir):
    sample = os.path.join(out_dir, RAW_SAMPLE)
    if not os.path.exists(sample):
        print(f"SKIP: {RAW_SAMPLE} not found - run make-raw-sample.py first")
        return
    start = time.time()
    size = os.path.getsize(sample)
    tags = find_tags(sample)
    print(f"{RAW_SAMPLE}: {len(tags):,} tags in {time.time() - start:.0f}s")

    # The catalogues tag one line each; keep their bounds and every TAG_ landmark, not each case.
    landmarks = [t for t in tags if t[2].startswith("TAG_") or t[2].endswith("_START") or t[2].endswith("_END")]
    lines = [f"# Lines around every landmark tag in {RAW_SAMPLE}. Open with:",
             f"#   Argonaut {RAW_SAMPLE} --lines --filter filter-raw-lines.txt",
             "# The BIGLINE entries select a ~100MB line each."]
    for _, line, name in landmarks:
        lines.append(f"{max(1, line - 1)}-{line + 1}  # {name}")
    write(os.path.join(out_dir, "filter-raw-lines.txt"), "\n".join(lines) + "\n")

    entries = [f"# Byte ranges around the landmarks in {RAW_SAMPLE} (0-based, last byte included). Open with:",
               f"#   Argonaut {RAW_SAMPLE} --bytes --filter filter-raw-bytes.txt"]
    for offset, _, name in landmarks:
        if name.endswith("_MID"):
            entries.append(f"{offset - 32 * 1024}+64k  # inside a ~100MB line, either side of {name}")
        elif name == "TAG_BINARY":
            entries.append(f"0x{offset:X}+0x2000  # {name} and into the binary block, in hex")
        else:
            entries.append(f"{max(0, offset - 200)}-{min(size - 1, offset + 300)}  # {name}")

    # A start inside a multi-byte UTF-8 character: the view snaps it back to the character's start.
    with open(sample, "rb") as f:
        head = f.read(4 * 1024**2)
    lead = next((i for i, b in enumerate(head) if b >= 0xE0 and i > 0), None)
    if lead is not None:
        entries.append(f"{lead + 1}+400  # starts on a continuation byte of a 3-4 byte character")
    entries.append("3g+4k  # a g suffix: 3,000,000,000 onwards")
    entries.append(f"{size - 100}-  # to the end")
    write(os.path.join(out_dir, "filter-raw-bytes.txt"), "\n".join(entries) + "\n")


def standalone_filters(out_dir):
    write(os.path.join(out_dir, "filter-empty.txt"), """\
# Nothing selected yet: the view is not opened, and a toast says why.
# Add an entry while a selection from this file is open and the view follows it.

""")
    write(os.path.join(out_dir, "filter-invalid.txt"), """\
# The entry on line 4 cannot be read, so the whole filter fails, naming that line.
1-10

20-x
""")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dir", default=DEFAULT_DIR, help="where the samples are and the filters go (default ~/testData)")
    out_dir = os.path.expanduser(parser.parse_args().dir)
    os.makedirs(out_dir, exist_ok=True)
    csv_filters(out_dir)
    raw_filters(out_dir)
    standalone_filters(out_dir)
    print(f"DONE -> {out_dir}")


if __name__ == "__main__":
    main()
