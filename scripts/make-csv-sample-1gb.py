#!/usr/bin/env python3
"""Generates a clean ~1GB CSV file for the Argonaut CSV viewer - no errors, consistent
column count throughout, for baseline (non-corrupted) large-file testing.

Columns: id,name,email,created_at,amount,active,description
"""
import os, random, time
from datetime import datetime, timedelta

OUT = os.path.expanduser("~/testData/sample-1gb.csv")
TARGET = 1 * 1024**3
CHECKPOINT_EVERY = 100 * 1024**2

random.seed(42)
FIRST_NAMES = "alice bob carol dave erin frank grace heidi ivan judy karl liam mona nate olive".split()
LAST_NAMES = "smith jones brown taylor wilson davies evans thomas roberts walker white hall".split()
WORDS = ("alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima "
         "mike november oscar papa quebec romeo sierra tango uniform victor whiskey").split()

HEADER = b"id,name,email,created_at,amount,active,description\n"
EPOCH = datetime(2020, 1, 1)


def make_row(i: int) -> bytes:
    first = random.choice(FIRST_NAMES)
    last = random.choice(LAST_NAMES)
    name = f"{first.capitalize()} {last.capitalize()}"
    email = f"{first}.{last}{i}@example.com"
    created_at = (EPOCH + timedelta(seconds=random.randint(0, 5 * 365 * 24 * 3600))).isoformat()
    amount = round(random.uniform(0, 10000), 2)
    active = "true" if random.random() < 0.7 else "false"
    description = " ".join(random.choice(WORDS) for _ in range(random.randint(3, 8)))
    return f"{i},{name},{email},{created_at},{amount},{active},{description}\n".encode()


def main():
    start = time.time()
    written = 0
    i = 0
    next_checkpoint = CHECKPOINT_EVERY

    with open(OUT, "wb", buffering=1024 * 1024) as f:
        def put(data: bytes):
            nonlocal written
            f.write(data)
            written += len(data)

        put(HEADER)

        while written < TARGET:
            put(make_row(i))
            i += 1

            if written >= next_checkpoint:
                print(f"{written / 1024**3:.2f} GiB written ({time.time() - start:.0f}s)", flush=True)
                next_checkpoint += CHECKPOINT_EVERY

    print(f"DONE: {written:,} bytes, {i:,} rows in {time.time() - start:.0f}s -> {OUT}", flush=True)


if __name__ == "__main__":
    main()
