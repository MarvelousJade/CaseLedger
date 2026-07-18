let
    RefreshedAt = #table(type table [refreshed_at_utc=datetimezone], {{DateTimeZone.FixedUtcNow()}})
in
    RefreshedAt
