let
    Source = Sql.Database(SqlServerHost, SqlServerDatabase),
    Mart = Source{[Schema="mart", Item="vw_SLACompliance"]}[Data],
    Typed = Table.TransformColumnTypes(Mart, {{"snapshot_date", type date}, {"sla_compliance_percentage", Percentage.Type}})
in
    Typed
