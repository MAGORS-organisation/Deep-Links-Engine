-- Advances the watermark of one rollup. Parameters: @name, @coveredThrough, @now.
--
-- The watermark never moves backwards: a job that failed halfway and re-ran with an earlier bound
-- must not make the reporting store trust less data than it already could.

INSERT INTO analytics_rollup_state (name, covered_through, updated_at)
VALUES (@name, @coveredThrough, @now)
ON CONFLICT (name)
DO UPDATE SET covered_through = greatest(analytics_rollup_state.covered_through,
                                         EXCLUDED.covered_through),
              updated_at      = EXCLUDED.updated_at;
