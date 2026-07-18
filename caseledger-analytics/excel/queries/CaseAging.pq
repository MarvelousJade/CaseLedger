let
    Source = Sql.Database(SqlServerHost, SqlServerDatabase),
    Mart = Source{[Schema="mart", Item="vw_CaseAging"]}[Data],
    Typed = Table.TransformColumnTypes(Mart, {{"snapshot_date", type date}, {"due_at", type datetime}})
in
    Typed
