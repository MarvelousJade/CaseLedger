from __future__ import annotations

from functools import cached_property

from pydantic import Field, SecretStr, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict
from sqlalchemy import URL


class Settings(BaseSettings):
    """Validated runtime configuration sourced from CASELEDGER_* variables."""

    model_config = SettingsConfigDict(
        env_prefix="CASELEDGER_",
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    source_url: str = (
        "postgresql+psycopg://caseledger_analytics:caseledger-analytics-local@"
        "localhost:6543/caseledger_source"
    )
    source_admin_url: str = (
        "postgresql+psycopg://caseledger:caseledger-local@localhost:6543/caseledger_source"
    )
    sqlserver_host: str = "localhost"
    sqlserver_port: int = Field(default=14333, ge=1, le=65535)
    sqlserver_database: str = "CaseLedgerAnalytics"
    sqlserver_user: str = "sa"
    sqlserver_password: SecretStr = SecretStr("CaseLedger_Analytics_2026!")
    sqlserver_driver: str = "ODBC Driver 18 for SQL Server"
    pipeline_name: str = "caseledger-python-incremental"
    source_entity: str = "caseledger"
    log_level: str = "INFO"

    @field_validator("sqlserver_database")
    @classmethod
    def validate_database_name(cls, value: str) -> str:
        if not value.replace("_", "").isalnum():
            raise ValueError("sqlserver_database may contain only letters, digits, and underscores")
        return value

    @field_validator("log_level")
    @classmethod
    def normalize_log_level(cls, value: str) -> str:
        normalized = value.upper()
        if normalized not in {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"}:
            raise ValueError("log_level is not a recognized Python logging level")
        return normalized

    @cached_property
    def warehouse_url(self) -> URL:
        return self.sqlserver_url(self.sqlserver_database)

    @cached_property
    def master_url(self) -> URL:
        return self.sqlserver_url("master")

    def sqlserver_url(self, database: str) -> URL:
        return URL.create(
            "mssql+pyodbc",
            username=self.sqlserver_user,
            password=self.sqlserver_password.get_secret_value(),
            host=self.sqlserver_host,
            port=self.sqlserver_port,
            database=database,
            query={
                "driver": self.sqlserver_driver,
                "Encrypt": "yes",
                "TrustServerCertificate": "yes",
            },
        )
