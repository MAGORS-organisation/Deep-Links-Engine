-- Advances the watermark of one rollup. Parameters: @name, @coveredFrom, @coveredThrough, @now.
--
-- The upper bound never moves backwards: a job that failed halfway and re-ran with an earlier bound
-- must not make the reporting store trust less data than it already could.
--
-- The lower bound only ever moves backwards. It is the earliest instant this rollup has ever
-- aggregated, and a report is answered from the rollup only when its window sits inside
-- [covered_from, covered_through]: a rollup that starts a week ago cannot be used to answer a
-- question about last month, which it would answer with zeros. `least` ignores NULL, so a row
-- written before the column existed acquires its first lower bound here.

INSERT INTO analytics_rollup_state (name, covered_from, covered_through, updated_at)
VALUES (@name, @coveredFrom, @coveredThrough, @now)
ON CONFLICT (name)
DO UPDATE SET covered_from    = least(analytics_rollup_state.covered_from,
                                      EXCLUDED.covered_from),
              covered_through = greatest(analytics_rollup_state.covered_through,
                                         EXCLUDED.covered_through),
              updated_at      = EXCLUDED.updated_at;
