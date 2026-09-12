-- Recomputes whole hourly buckets of the install and conversion rollup.
--
-- Parameters: @from, @to (hour-aligned, half open), @now.
--
-- Installs are bucketed by first_open_at and conversions by the SDK event timestamp, so the two
-- sides are joined on the bucket rather than on the install: an install in one hour and its
-- conversion in the next belong to different buckets, and pretending otherwise would move revenue
-- backwards in time. link_id = 0 carries the installs and conversions that are not tied to a link.

WITH install_agg AS (
    SELECT date_trunc('hour', ins.first_open_at, 'UTC')            AS bucket,
           ins.tenant_id                                           AS tenant_id,
           coalesce(a.link_id, 0)                                  AS link_id,
           lower(coalesce(nullif(ins.platform, ''), 'unknown'))    AS platform,
           count(*)::bigint                                        AS installs,
           count(*) FILTER (
               WHERE a.id IS NOT NULL AND a.match_type <> 'none'
           )::bigint                                               AS attributed
    FROM installs AS ins
    LEFT JOIN attributions AS a ON a.install_id = ins.id
    WHERE ins.first_open_at >= @from
      AND ins.first_open_at <  @to
    GROUP BY 1, 2, 3, 4
),
conversion_agg AS (
    SELECT date_trunc('hour', s.occurred_at, 'UTC')                AS bucket,
           s.tenant_id                                             AS tenant_id,
           coalesce(s.link_id, 0)                                  AS link_id,
           lower(coalesce(nullif(ins.platform, ''), 'unknown'))    AS platform,
           count(*)::bigint                                        AS conversions,
           sum(s.event_value)::numeric(18, 4)                            AS conversion_value
    FROM sdk_events AS s
    LEFT JOIN installs AS ins
           ON ins.app_id = s.app_id
          AND ins.install_id = s.install_id
    WHERE s.occurred_at >= @from
      AND s.occurred_at <  @to
      AND s.event_type = 'conversion'
    GROUP BY 1, 2, 3, 4
),
merged AS (
    SELECT bucket,
           tenant_id,
           link_id,
           platform,
           install_agg.installs,
           install_agg.attributed,
           conversion_agg.conversions,
           conversion_agg.conversion_value
    FROM install_agg
    FULL OUTER JOIN conversion_agg USING (bucket, tenant_id, link_id, platform)
)
INSERT INTO install_rollup_hourly
    (bucket, tenant_id, link_id, platform, campaign_id,
     installs, attributed, conversions, conversion_value, updated_at)
SELECT m.bucket,
       m.tenant_id,
       m.link_id,
       m.platform,
       l.campaign_id,
       coalesce(m.installs, 0),
       coalesce(m.attributed, 0),
       coalesce(m.conversions, 0),
       m.conversion_value,
       @now
FROM merged AS m
LEFT JOIN links AS l ON l.id = m.link_id
ON CONFLICT (bucket, tenant_id, link_id, platform)
DO UPDATE SET campaign_id      = EXCLUDED.campaign_id,
              installs         = EXCLUDED.installs,
              attributed       = EXCLUDED.attributed,
              conversions      = EXCLUDED.conversions,
              conversion_value = EXCLUDED.conversion_value,
              updated_at       = EXCLUDED.updated_at;
