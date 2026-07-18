from __future__ import annotations

from sqlalchemy import Engine, create_engine

from etl.config import Settings


def create_source_engine(settings: Settings) -> Engine:
    return create_engine(settings.source_url, pool_pre_ping=True)


def create_source_admin_engine(settings: Settings) -> Engine:
    """Local demo-only writer; production pipeline code never calls this function."""

    return create_engine(settings.source_admin_url, pool_pre_ping=True)


def create_warehouse_engine(settings: Settings, *, master: bool = False) -> Engine:
    return create_engine(
        settings.master_url if master else settings.warehouse_url,
        pool_pre_ping=True,
        fast_executemany=True,
    )
