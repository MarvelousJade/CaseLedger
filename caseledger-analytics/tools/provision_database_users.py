from __future__ import annotations

import os

from etl.config import Settings
from etl.db import create_warehouse_engine


def sql_string(value: str) -> str:
    return "N'" + value.replace("'", "''") + "'"


def create_or_rotate_user(connection: object, user: str, password: str) -> None:
    password_literal = sql_string(password)
    connection.exec_driver_sql(
        f"""
        IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{user}')
            ALTER USER [{user}] WITH PASSWORD = {password_literal};
        ELSE
            CREATE USER [{user}] WITH PASSWORD = {password_literal};
        """
    )


def add_role_member(connection: object, role: str, user: str) -> None:
    connection.exec_driver_sql(
        f"""
        IF NOT EXISTS (
            SELECT 1
            FROM sys.database_role_members membership
            JOIN sys.database_principals role_principal
              ON role_principal.principal_id = membership.role_principal_id
            JOIN sys.database_principals member_principal
              ON member_principal.principal_id = membership.member_principal_id
            WHERE role_principal.name = N'{role}'
              AND member_principal.name = N'{user}'
        )
            ALTER ROLE [{role}] ADD MEMBER [{user}];
        """
    )


def main() -> None:
    etl_password = os.environ["CASELEDGER_ETL_USER_PASSWORD"]
    report_password = os.environ["CASELEDGER_REPORT_USER_PASSWORD"]
    engine = create_warehouse_engine(Settings())
    try:
        with engine.begin() as connection:
            connection.exec_driver_sql(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.database_principals
                    WHERE name = N'caseledger_etl_executor'
                )
                    CREATE ROLE [caseledger_etl_executor];

                GRANT SELECT ON SCHEMA::ctl TO [caseledger_etl_executor];
                GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::stg TO [caseledger_etl_executor];
                GRANT SELECT ON SCHEMA::dw TO [caseledger_etl_executor];
                GRANT EXECUTE ON SCHEMA::ctl TO [caseledger_etl_executor];
                GRANT EXECUTE ON SCHEMA::dw TO [caseledger_etl_executor];
                """
            )
            create_or_rotate_user(connection, "caseledger_etl", etl_password)
            create_or_rotate_user(connection, "caseledger_report", report_password)
            add_role_member(connection, "caseledger_etl_executor", "caseledger_etl")
            add_role_member(connection, "caseledger_report_reader", "caseledger_report")
    finally:
        engine.dispose()

    print("Provisioned least-privilege ETL and Power BI database users.")


if __name__ == "__main__":
    main()
