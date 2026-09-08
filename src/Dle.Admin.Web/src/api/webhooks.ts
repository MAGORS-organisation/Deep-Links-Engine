import { api } from './client';
import type {
  CreateWebhookRequest,
  TestWebhookResponse,
  WebhookCreatedResponse,
  WebhookDeliveryResponse,
  WebhookResponse,
} from './types';

const base = '/api/v1/webhooks';

/** These routes answer plain arrays rather than a page (WebhookEndpointExtensions). */
export const webhooks = {
  list(onlyActive = false, signal?: AbortSignal): Promise<WebhookResponse[]> {
    return api(base, { query: { onlyActive }, signal });
  },

  /** The secret in the answer is shown once (§B.5.2, §E.4.1 K4). */
  create(body: CreateWebhookRequest): Promise<WebhookCreatedResponse> {
    return api(base, { method: 'POST', body });
  },

  remove(id: string): Promise<void> {
    return api(`${base}/${encodeURIComponent(id)}`, { method: 'DELETE' });
  },

  /** Synchronous by design: the operator learns immediately whether the endpoint answered. */
  test(id: string): Promise<TestWebhookResponse> {
    return api(`${base}/${encodeURIComponent(id)}/test`, { method: 'POST', idempotent: false });
  },

  deliveries(
    subscriptionId?: string,
    limit = 50,
    includePayload = false,
    signal?: AbortSignal,
  ): Promise<WebhookDeliveryResponse[]> {
    return api(`${base}/deliveries`, { query: { subscriptionId, limit, includePayload }, signal });
  },
};
