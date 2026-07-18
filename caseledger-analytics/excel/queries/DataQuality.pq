let
    Source = Sql.Database(SqlServerHost, SqlServerDatabase),
    Mart = Source{[Schema="mart", Item="vw_DataQuality"]}[Data]
in
    Mart
