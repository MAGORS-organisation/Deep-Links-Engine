-- Lists the range partitions of a partitioned table together with their upper bound.
--
-- Parameters: @parentTable.
--
-- Retention drops whole partitions rather than deleting rows (§E.6.3): a DELETE on a hundred
-- million rows rewrites indexes, bloats the heap and leaves the data recoverable from the table
-- until VACUUM catches up, while DETACH plus DROP is a catalogue operation that actually removes
-- the file. Deciding which partitions are expired therefore means reading their bounds.
--
-- upper_bound is NULL for anything this query cannot prove is a plain half-open range — a DEFAULT
-- partition, a MAXVALUE bound, a list or hash partition. The caller treats a NULL upper bound as
-- "never drop", so an unrecognised partition shape fails closed.

SELECT child.relname AS "PartitionName",
       CASE
           WHEN bound.expr LIKE 'FOR VALUES FROM (%) TO (%)'
                AND bound.expr NOT LIKE '%MAXVALUE%'
                AND bound.expr NOT LIKE '%MINVALUE%'
           THEN (regexp_match(bound.expr, 'TO \(''([^'']+)''\)'))[1]::timestamptz
           ELSE NULL
       END AS "UpperBound"
FROM pg_class AS parent
JOIN pg_namespace AS ns ON ns.oid = parent.relnamespace
JOIN pg_inherits AS inh ON inh.inhparent = parent.oid
JOIN pg_class AS child ON child.oid = inh.inhrelid
CROSS JOIN LATERAL (SELECT pg_get_expr(child.relpartbound, child.oid) AS expr) AS bound
WHERE parent.relname = @parentTable
  AND ns.nspname = current_schema()
  AND child.relispartition
ORDER BY child.relname;
