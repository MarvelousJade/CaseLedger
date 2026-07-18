from __future__ import annotations

from etl.deploy import is_database_already_exists_error, split_sql_batches


def test_split_sql_batches_only_splits_standalone_go() -> None:
    script = "SELECT 'go';\nGO\nSELECT 2;\n go -- batch\nSELECT 3;"

    assert list(split_sql_batches(script)) == [
        "SELECT 'go';",
        "SELECT 2;",
        "SELECT 3;",
    ]


def test_database_already_exists_error_matches_sql_server_code() -> None:
    error = Exception(
        "[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]Database already exists. (1801)"
    )

    assert is_database_already_exists_error(error)
    assert not is_database_already_exists_error(Exception("Login failed. (18456)"))
