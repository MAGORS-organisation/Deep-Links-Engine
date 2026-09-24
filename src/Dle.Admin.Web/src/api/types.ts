/**
 * Wire types of the control plane, mirrored from src/Dle.Domain/Contracts/*.cs and the
 * feature-local contracts in src/Dle.Control/Features. The HTTP JSON format is snake_case for
 * properties and for enumeration values (AddDleControlCore); query strings use the parameter
 * names the minimal API declares, which is why some of them are camelCase.
 *
 * Nothing in this file is invented: every member exists on the server. When a DTO gains a member
 * it is added here in the same change.
 */

// ---- Shared ------------------------------------------------------------------------------------

export interface PagedResponse<T> {
  items: T[];
  next_cursor?: string | null;
  total?: number | null;
}

/** RFC 9457 problem document. `type` is one of ProblemCodes (src/api/problems.ts). */
export interface ProblemDocument {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
}

/** Open Graph metadata (src/Dle.Domain/Links/OgMeta.cs). */
export interface OgMeta {
  title?: string | null;
  description?: string | null;
  image_url?: string | null;
  site_name?: string | null;
  type?: string | null;
  twitter_card?: string | null;
}

// ---- Routing rules (src/Dle.Domain/Routing) ----------------------------------------------------

export type Platform = 'ios' | 'android' | 'desktop' | 'other' | 'unknown';

export const PLATFORMS: readonly Platform[] = ['ios', 'android', 'desktop', 'other', 'unknown'];

/** Canonical channel names; must mirror src/Dle.Domain/Routing/ChannelNames.cs exactly. */
export const CHANNELS = [
  'unknown',
  'browser',
  'crawler',
  'in_app_fb',
  'in_app_ig',
  'in_app_tiktok',
  'in_app_linkedin',
  'in_app_snapchat',
  'in_app_x',
  'in_app_whatsapp',
  'in_app_telegram',
  'in_app_pinterest',
  'in_app_other',
  'app',
] as const;

export type Channel = (typeof CHANNELS)[number];

export type RoutingActionKind = 'web' | 'app_or_store' | 'store_only' | 'app_only' | 'block';

export const ROUTING_ACTIONS: readonly RoutingActionKind[] = [
  'web',
  'app_or_store',
  'store_only',
  'app_only',
  'block',
];

export type InterstitialMode = 'auto' | 'always' | 'never';

export const INTERSTITIAL_MODES: readonly InterstitialMode[] = ['auto', 'always', 'never'];

export interface VersionPredicate {
  eq?: string | null;
  gt?: string | null;
  gte?: string | null;
  lt?: string | null;
  lte?: string | null;
}

export interface TimeWindowPredicate {
  from?: string | null;
  to?: string | null;
  hours_utc?: number[] | null;
  days_of_week_utc?: number[] | null;
}

export interface AbVariant {
  variant: string;
  percent: number;
}

export interface RuleCondition {
  platform?: Platform[] | null;
  os_version?: VersionPredicate | null;
  app_version?: VersionPredicate | null;
  country?: string[] | null;
  region?: string[] | null;
  language?: string[] | null;
  channel?: Channel[] | null;
  time_window?: TimeWindowPredicate | null;
  ab?: AbVariant[] | null;
}

export interface RuleAction {
  action: RoutingActionKind;
  url?: string | null;
  deeplink_path?: string | null;
  store_url?: string | null;
  referrer_template?: string | null;
  interstitial?: InterstitialMode;
}

/** A rule whose `when` is null or absent is the default rule (TC-105). */
export interface RoutingRule {
  id: string;
  when?: RuleCondition | null;
  then: RuleAction;
}

// ---- Links -------------------------------------------------------------------------------------

export interface CreateLinkRequest {
  domain_id: string;
  slug?: string | null;
  title?: string | null;
  description?: string | null;
  target_url: string;
  deeplink_path?: string | null;
  routing_rules: RoutingRule[];
  og?: OgMeta | null;
  utm?: Record<string, string>;
  campaign_id?: string | null;
  tags?: string[];
  starts_at?: string | null;
  expires_at?: string | null;
  expired_url?: string | null;
  is_active?: boolean;
}

export interface UpdateLinkRequest {
  title?: string | null;
  description?: string | null;
  target_url?: string | null;
  deeplink_path?: string | null;
  routing_rules?: RoutingRule[] | null;
  og?: OgMeta | null;
  utm?: Record<string, string> | null;
  campaign_id?: string | null;
  tags?: string[] | null;
  starts_at?: string | null;
  expires_at?: string | null;
  expired_url?: string | null;
  is_active?: boolean | null;
  change_note?: string | null;
}

export interface LinkResponse {
  id: string;
  tenant_id: string;
  domain_id: string;
  host: string;
  slug: string;
  short_url: string;
  qr_url: string;
  title?: string | null;
  description?: string | null;
  target_url: string;
  deeplink_path?: string | null;
  routing_rules: RoutingRule[];
  og?: OgMeta | null;
  utm: Record<string, string>;
  campaign_id?: string | null;
  tags: string[];
  is_active: boolean;
  starts_at?: string | null;
  expires_at?: string | null;
  expired_url?: string | null;
  quarantined_at?: string | null;
  version: number;
  created_at: string;
  updated_at: string;
}

export interface LinkListQuery {
  search?: string;
  tags?: string;
  domainId?: string;
  campaignId?: string;
  isActive?: boolean;
  includeArchived?: boolean;
  limit?: number;
  cursor?: string;
}

export interface LinkVersionSummary {
  version: number;
  changed_by?: string | null;
  changed_at: string;
  change_note?: string | null;
  snapshot: unknown;
}

export interface SimulateRequest {
  user_agent?: string | null;
  platform?: string | null;
  country?: string | null;
  region?: string | null;
  language?: string | null;
  os_version?: string | null;
  app_version?: string | null;
  channel?: string | null;
  at?: string | null;
  click_id?: string | null;
}

export interface SimulateResponse {
  matched_rule_id: string;
  decision: string;
  url?: string | null;
  deeplink_path?: string | null;
  store_url?: string | null;
  ab_bucket?: number | null;
  ab_variant?: string | null;
  consent_mode: string;
  trace: string[];
}

export interface LinkTemplateRequest {
  name: string;
  utm?: Record<string, string>;
  routing_rules?: RoutingRule[];
  target_url?: string | null;
  deeplink_path?: string | null;
  og?: OgMeta | null;
  tags?: string[];
}

export interface LinkTemplateResponse {
  id: string;
  name: string;
  utm: Record<string, string>;
  routing_rules: RoutingRule[];
  target_url?: string | null;
  deeplink_path?: string | null;
  og?: OgMeta | null;
  tags: string[];
  created_at: string;
}

// ---- Domains -----------------------------------------------------------------------------------

export type VerificationStatus = 'pending' | 'ok' | 'failed' | string;

export interface CreateDomainRequest {
  host: string;
  is_default?: boolean;
  consent_mode_override?: string | null;
  default_language?: string | null;
  default_og?: OgMeta | null;
}

export interface UpdateDomainRequest {
  is_default?: boolean | null;
  is_active?: boolean | null;
  consent_mode_override?: string | null;
  default_og?: OgMeta | null;
}

export interface DomainResponse {
  id: string;
  tenant_id: string;
  host: string;
  is_default: boolean;
  is_active: boolean;
  tls_status: VerificationStatus;
  aasa_status: VerificationStatus;
  assetlinks_status: VerificationStatus;
  last_verified_at?: string | null;
  consent_mode_override?: string | null;
  created_at: string;
}

export interface VerificationCheckResult {
  kind: 'dns' | 'tls' | 'aasa' | 'assetlinks' | string;
  status: 'ok' | 'warning' | 'failed' | string;
  codes: string[];
  detail?: string | null;
}

export interface DomainVerificationResponse {
  domain_id: string;
  host: string;
  ok: boolean;
  checked_at: string;
  checks: VerificationCheckResult[];
  propagation_notice?: string | null;
}

export interface DomainVerificationRecord {
  kind: string;
  status: string;
  http_status?: number | null;
  redirect_count: number;
  checked_at: string;
}

// ---- Apps --------------------------------------------------------------------------------------

export interface CreateAppRequest {
  platform: 'ios' | 'android';
  bundle_id: string;
  team_id?: string | null;
  cert_fingerprints?: string[];
  store_id?: string | null;
  store_url?: string | null;
  custom_scheme?: string | null;
  min_app_version?: string | null;
  app_clip_bundle_id?: string | null;
  domain_ids?: string[];
}

export interface UpdateAppRequest {
  team_id?: string | null;
  cert_fingerprints?: string[] | null;
  play_signing_fingerprints?: string[] | null;
  store_id?: string | null;
  store_url?: string | null;
  custom_scheme?: string | null;
  min_app_version?: string | null;
  app_clip_bundle_id?: string | null;
  domain_ids?: string[] | null;
}

export interface AppResponse {
  id: string;
  tenant_id: string;
  platform: string;
  bundle_id: string;
  team_id?: string | null;
  cert_fingerprints: string[];
  store_id?: string | null;
  store_url?: string | null;
  custom_scheme?: string | null;
  app_clip_bundle_id?: string | null;
  domain_ids: string[];
  warnings: string[];
  created_at: string;
}

export interface SdkKeyCreatedResponse {
  id: string;
  app_id: string;
  secret: string;
  prefix: string;
  created_at: string;
}

export interface SdkKeyResponse {
  id: string;
  app_id: string;
  prefix: string;
  is_active: boolean;
  created_at: string;
}

// ---- Tenants and keys --------------------------------------------------------------------------

export type ConsentMode = 'off' | 'aggregate_only' | 'full';

export const CONSENT_MODES: readonly ConsentMode[] = ['off', 'aggregate_only', 'full'];

export interface CreateTenantRequest {
  slug: string;
  name: string;
  consent_mode?: ConsentMode | null;
}

export interface UpdateTenantRequest {
  name?: string | null;
  consent_mode?: ConsentMode | null;
  status?: 'active' | 'suspended' | null;
}

export interface TenantResponse {
  id: string;
  slug: string;
  name: string;
  status: string;
  consent_mode: string;
  created_at: string;
}

export type Role = 'owner' | 'admin' | 'editor' | 'viewer';

export const ROLES: readonly Role[] = ['owner', 'admin', 'editor', 'viewer'];

export interface CreateApiKeyRequest {
  name: string;
  role: Role;
  scopes?: string[];
  expires_at?: string | null;
}

export interface ApiKeyCreatedResponse {
  id: string;
  name: string;
  secret: string;
  prefix: string;
  role: string;
  expires_at?: string | null;
}

export interface ApiKeyResponse {
  id: string;
  name: string;
  prefix: string;
  role: string;
  scopes: string[];
  last_used_at?: string | null;
  expires_at?: string | null;
  revoked_at?: string | null;
  created_at: string;
}

// ---- Analytics ---------------------------------------------------------------------------------

export type Grain = 'hour' | 'day' | 'week' | 'month';

export interface AnalyticsQuery {
  from: string;
  to: string;
  grain?: Grain;
  link_id?: string;
  campaign_id?: string;
  country?: string;
  platform?: string;
  include_bots?: boolean;
  limit?: number;
}

export interface TimeSeriesPoint {
  bucket: string;
  clicks: number;
  installs: number;
  conversions: number;
}

export interface FunnelSummary {
  clicks: number;
  installs: number;
  attributed: number;
  conversions: number;
  conversion_rate: number;
}

export interface TimeSeriesResponse {
  grain: string;
  points: TimeSeriesPoint[];
  totals?: FunnelSummary | null;
}

export interface BreakdownRow {
  key: string;
  clicks: number;
  installs: number;
  value?: number | null;
}

export interface BreakdownResponse {
  dimension: string;
  rows: BreakdownRow[];
}

export type BreakdownDimension =
  | 'country'
  | 'region'
  | 'platform'
  | 'os_family'
  | 'device_class'
  | 'channel'
  | 'language';

export interface MatchTypeSummary {
  match_type: string;
  count: number;
  average_confidence: number;
}

/** ADR-008: the honest split. Never collapse these three numbers into one. */
export interface AttributionQualityResponse {
  deterministic: number;
  probabilistic: number;
  unmatched: number;
  average_probabilistic_confidence: number;
  by_match_type: MatchTypeSummary[];
}

// ---- Webhooks ----------------------------------------------------------------------------------

export const WEBHOOK_EVENT_TYPES = [
  'attribution.created',
  'link.created',
  'link.updated',
  'link.quarantined',
  'link.released',
  'domain.verification_failed',
] as const;

export type WebhookEventType = (typeof WEBHOOK_EVENT_TYPES)[number];

export interface CreateWebhookRequest {
  url: string;
  event_types: string[];
  is_active?: boolean;
}

export interface WebhookResponse {
  id: string;
  url: string;
  event_types: string[];
  is_active: boolean;
  created_at: string;
}

export interface WebhookCreatedResponse extends WebhookResponse {
  secret: string;
  signing_key_id?: string | null;
}

export interface WebhookDeliveryResponse {
  id: string;
  subscription_id: string;
  event_type: string;
  status: 'pending' | 'delivered' | 'failed' | 'dead' | string;
  attempt: number;
  response_code?: number | null;
  last_error?: string | null;
  next_attempt_at?: string | null;
  created_at: string;
  delivered_at?: string | null;
  payload?: string | null;
}

export interface TestWebhookResponse {
  delivered: boolean;
  outcome: 'delivered' | 'failed' | 'unsendable' | string;
  response_code?: number | null;
  error?: string | null;
  elapsed_ms: number;
  payload: string;
  signing_key_id?: string | null;
}

// ---- Abuse -------------------------------------------------------------------------------------

export const ABUSE_REASONS = ['phishing', 'malware', 'spam', 'illegal', 'copyright', 'other'] as const;

export const ABUSE_STATUSES = ['new', 'triaged', 'confirmed', 'rejected', 'resolved'] as const;

export type AbuseStatus = (typeof ABUSE_STATUSES)[number];

export interface AbuseReportDetailResponse {
  id: string;
  tenant_id: string;
  link_id: string;
  reason: string;
  details?: string | null;
  status: AbuseStatus | string;
  has_reporter_contact: boolean;
  created_at: string;
  resolved_at?: string | null;
  resolution_note?: string | null;
}

export interface TriageDecisionRequest {
  status: AbuseStatus;
  resolution_note: string;
}

export interface QuarantineDecisionRequest {
  reason: string;
}
