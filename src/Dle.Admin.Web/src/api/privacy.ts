import { api } from './client';
import type { InstallErasureResponse } from './types';

export const privacy = {
  /** C3 / GDPR Art. 17: erases all data tied to one end-user install_id within the caller's tenant. */
  eraseInstall(installId: string): Promise<InstallErasureResponse> {
    return api(`/api/v1/privacy/installs/${encodeURIComponent(installId.trim())}`, {
      method: 'DELETE',
      idempotent: false,
    });
  },
};
