from __future__ import annotations

from tools.provision_database_users import sql_string


def test_sql_string_escapes_single_quotes() -> None:
    assert sql_string("safe'password") == "N'safe''password'"
