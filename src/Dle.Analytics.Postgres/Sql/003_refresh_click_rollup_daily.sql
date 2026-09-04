-- Rolls whole days of click_rollup_hourly up into click_rollup_daily.
--
-- Parameters: @from, @to (day-aligned, half open), @now.
-- Reading the hourly rollup instead of click_events keeps the daily pass off the raw partitions
-- entirely; the caller guarantees day alignment so a partial day is never written as a complete
-- one.

INSERT INTO click_rollup_daily
    (bucket, tenant_id, link_id, country, platform, channel, is_bot, campaign_id, clicks, updated_at)
SELECT date_trunc('day', h.bucket, 'UTC') AS bucket,
       h.tenant_id,
       h.link_id,
       h.country,
       h.platform,
       h.channel,
       h.is_bot,
       h.campaign_id,
       sum(h.clicks)::bigint              AS clicks,
       @now                               AS updated_at
FROM click_rollup_hourly AS h
WHERE h.bucket >= @from
  AND h.bucket <  @to
GROUP BY 1, 2, 3, 4, 5, 6, 7, 8
ON CONFLICT (bucket, tenant_id, link_id, country, platform, channel, is_bot)
DO UPDATE SET clicks      = EXCLUDED.clicks,
              campaign_id = EXCLUDED.campaign_id,
              updated_at  = EXCLUDED.updated_at;
