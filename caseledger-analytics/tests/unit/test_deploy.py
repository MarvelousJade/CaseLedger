from __future__ import annotations

from etl.deploy import split_sql_batches


def test_split_sql_batches_only_splits_standalone_go() -> None:
    script = "SELECT 'go';\nGO\nSELECT 2;\n go -- batch\nSELECT 3;"

    assert list(split_sql_batches(script)) == [
        "SELECT 'go';",
        "SELECT 2;",
        "SELECT 3;",
    ]
