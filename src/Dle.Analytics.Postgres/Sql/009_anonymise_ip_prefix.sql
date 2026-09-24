-- Reduces older raw click events to hash-only form. Parameters: @cutoff.
--
-- FR-247 wants the click stream to become hash-only once it is older than the configured window.
-- ip_hash is already an HMAC under a daily rotating salt, so what is left to remove is ip_prefix,
-- the /24 or /48 network that is only stored under consent = full in the first place (§E.6.2).
-- Clearing it leaves a row that can still be counted but can no longer be narrowed to a network.
--
-- This runs only when Dle:Privacy:Retention:IpPrefixDays is configured. When it is not, the
-- prefix simply lives as long as the row does and disappears with the partition.

UPDATE click_events
SET ip_prefix = NULL
WHERE occurred_at < @cutoff
  AND ip_prefix IS NOT NULL;
