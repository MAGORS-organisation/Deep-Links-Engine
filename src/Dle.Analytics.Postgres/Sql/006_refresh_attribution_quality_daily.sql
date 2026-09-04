-- Recomputes whole days of the attribution quality rollup — the ADR-008 dashboard panel.
--
-- Parameters: @from, @to (day-aligned, half open), @now.
--
-- The driving table is installs, not attributions, on purpose. An install that no strategy could
-- tie to a click has no attribution row at all, and it is exactly that share ("8 % unmatched")
-- that a mobile measurement partner would quietly drop. The LEFT JOIN turns it into a match_type
-- of 'none' with a confidence of zero, so the rollup always sums to the number of installs.

WITH source AS (
    SELECT date_trunc('day', ins.first_open_at, 'UTC')  AS bucket,
           ins.tenant_id                                AS tenant_id,
           coalesce(a.link_id, 0)                       AS link_id,
           coalesce(a.match_type, 'none')               AS match_type,
           coalesce(a.confidence, 0)::numeric(4, 2)     AS confidence
    FROM installs AS ins
    LEFT JOIN attributions AS a ON a.install_id = ins.id
    WHERE ins.first_open_at >= @from
      AND ins.first_open_at <  @to
)
INSERT INTO attribution_quality_daily
    (bucket, tenant_id, link_id, match_type, matches, confidence_sum, updated_at)
SELECT bucket,
       tenant_id,
       link_id,
       match_type,
       count(*)::bigint                AS matches,
       sum(confidence)::numeric(14, 2) AS confidence_sum,
       @now                            AS updated_at
FROM source
GROUP BY 1, 2, 3, 4
ON CONFLICT (bucket, tenant_id, link_id, match_type)
DO UPDATE SET matches        = EXCLUDED.matches,
              confidence_sum = EXCLUDED.confidence_sum,
              updated_at     = EXCLUDED.updated_at;
