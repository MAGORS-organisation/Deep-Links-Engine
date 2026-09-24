-- Rolls whole days of install_rollup_hourly up into install_rollup_daily.
--
-- Parameters: @from, @to (day-aligned, half open), @now.

INSERT INTO install_rollup_daily
    (bucket, tenant_id, link_id, platform, campaign_id,
     installs, attributed, conversions, conversion_value, updated_at)
SELECT date_trunc('day', h.bucket, 'UTC') AS bucket,
       h.tenant_id,
       h.link_id,
       h.platform,
       max(h.campaign_id::text)::uuid     AS campaign_id,
       sum(h.installs)::bigint            AS installs,
       sum(h.attributed)::bigint          AS attributed,
       sum(h.conversions)::bigint         AS conversions,
       sum(h.conversion_value)            AS conversion_value,
       @now                               AS updated_at
FROM install_rollup_hourly AS h
WHERE h.bucket >= @from
  AND h.bucket <  @to
GROUP BY 1, 2, 3, 4
ON CONFLICT (bucket, tenant_id, link_id, platform)
DO UPDATE SET campaign_id      = EXCLUDED.campaign_id,
              installs         = EXCLUDED.installs,
              attributed       = EXCLUDED.attributed,
              conversions      = EXCLUDED.conversions,
              conversion_value = EXCLUDED.conversion_value,
              updated_at       = EXCLUDED.updated_at;
