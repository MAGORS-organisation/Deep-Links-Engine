-- Deletes aggregates older than the configured aggregate retention. Parameters: @cutoff.
--
-- The rollups are not partitioned — they are small, and partitioning them would buy a cheaper
-- delete at the cost of a partition maintenance job for tables that hold a few million rows at
-- most. A DELETE is the right tool here, unlike on the raw click stream.

DELETE FROM click_rollup_hourly WHERE bucket < @cutoff;
DELETE FROM click_rollup_daily WHERE bucket < @cutoff;
DELETE FROM install_rollup_hourly WHERE bucket < @cutoff;
DELETE FROM install_rollup_daily WHERE bucket < @cutoff;
DELETE FROM attribution_quality_daily WHERE bucket < @cutoff;
