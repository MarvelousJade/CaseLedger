let
    Source = Sql.Database(SqlServerHost, SqlServerDatabase),
    Mart = Source{[Schema="mart", Item="vw_OperationsDaily"]}[Data],
    Typed = Table.TransformColumnTypes(Mart, {{"calendar_date", type date}})
in
    Typed
