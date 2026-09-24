-- Records one execution of the retention job (FR-247, §E.6.3: the job's own run has to be
-- auditable). Written whether the run succeeded, did nothing, or failed.
--
-- Parameters: @id, @startedAt, @finishedAt, @rawDays, @aggregatedDays, @ipPrefixDays, @dryRun,
--             @partitionsDropped, @rowsAnonymised, @rollupRowsDeleted, @status, @error.
--
-- Note what is deliberately absent: no identifier of any end user, no IP, no click id. §E.6.3
-- keeps the audit trail free of data subjects precisely so that the right to erasure and the
-- immutable audit log do not contradict each other.

INSERT INTO analytics_retention_runs
    (id, started_at, finished_at, raw_days, aggregated_days, ip_prefix_days, dry_run,
     partitions_dropped, rows_anonymised, rollup_rows_deleted, status, error)
VALUES
    (@id, @startedAt, @finishedAt, @rawDays, @aggregatedDays, @ipPrefixDays, @dryRun,
     @partitionsDropped, @rowsAnonymised, @rollupRowsDeleted, @status, @error);
