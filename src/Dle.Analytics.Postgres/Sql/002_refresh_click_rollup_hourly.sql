-- Recomputes whole hourly buckets of the click rollup from the partitioned click_events table.
--
-- Parameters: @from, @to (hour-aligned, half open), @now.
-- The whole bucket is recomputed and overwritten rather than incremented, so re-running the job
-- over an overlapping window is idempotent — which is what lets the rollup service re-scan a few
-- hours on every pass and still pick up late arrivals without double counting.
--
-- campaign_id is grouped rather than aggregated: it is functionally dependent on link_id, so
-- adding it to the grouping cannot split a bucket, and grouping avoids needing a min/max aggregate
-- for uuid.

INSERT INTO click_rollup_hourly
    (bucket, tenant_id, link_id, country, platform, channel, is_bot, campaign_id, clicks, updated_at)
SELECT date_trunc('hour', e.occurred_at, 'UTC')                AS bucket,
       e.tenant_id,
       e.link_id,
       coalesce(nullif(e.country, ''), 'unknown')               AS country,
       dle_platform_of(e.os_family, e.device_class)             AS platform,
       coalesce(nullif(e.channel, ''), 'unknown')               AS channel,
       e.is_bot,
       l.campaign_id,
       count(*)::bigint                                         AS clicks,
       @now                                                     AS updated_at
FROM click_events AS e
LEFT JOIN links AS l ON l.id = e.link_id
WHERE e.occurred_at >= @from
  AND e.occurred_at <  @to
GROUP BY 1, 2, 3, 4, 5, 6, 7, 8
ON CONFLICT (bucket, tenant_id, link_id, country, platform, channel, is_bot)
DO UPDATE SET clicks      = EXCLUDED.clicks,
              campaign_id = EXCLUDED.campaign_id,
              updated_at  = EXCLUDED.updated_at;
