from __future__ import annotations

import pytest
from pydantic import ValidationError

from etl.config import Settings


def test_sql_server_url_keeps_password_out_of_rendered_url() -> None:
    settings = Settings(sqlserver_password="A-Strong-Password!2026")

    rendered = settings.warehouse_url.render_as_string(hide_password=True)

    assert "A-Strong-Password!2026" not in rendered
    assert settings.sqlserver_database in rendered


def test_database_name_rejects_identifier_injection() -> None:
    with pytest.raises(ValidationError):
        Settings(sqlserver_database="analytics]; DROP DATABASE master;--")


def test_log_level_is_normalized() -> None:
    assert Settings(log_level="warning").log_level == "WARNING"
