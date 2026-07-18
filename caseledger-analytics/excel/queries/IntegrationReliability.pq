let
    Source = Sql.Database(SqlServerHost, SqlServerDatabase),
    Mart = Source{[Schema="mart", Item="vw_IntegrationReliability"]}[Data],
    Typed = Table.TransformColumnTypes(Mart, {{"calendar_date", type date}, {"delivery_success_percentage", Percentage.Type}, {"retry_percentage", Percentage.Type}})
in
    Typed
