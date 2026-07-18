from __future__ import annotations

import argparse

from etl.config import Settings
from etl.db import create_source_admin_engine
from etl.synthetic import seed_source


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate deterministic CaseLedger source data")
    parser.add_argument("--cases", type=int, default=160)
    args = parser.parse_args()
    engine = create_source_admin_engine(Settings())
    try:
        print(seed_source(engine, args.cases))
    finally:
        engine.dispose()


if __name__ == "__main__":
    main()
