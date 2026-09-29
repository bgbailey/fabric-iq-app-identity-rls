import type { DemoCatalog } from './api';

export interface Availability {
  enabled: boolean;
  label: string;
  reason: string;
}

export function availability(catalog: DemoCatalog | null, now: number): Availability {
  if (!catalog) {
    return { enabled: false, label: 'Backend unavailable', reason: 'Load the local demo configuration before running a question.' };
  }
  if (!catalog.liveEnabled) {
    return {
      enabled: false,
      label: 'Not configured',
      reason: 'Live IQ schema discovery, DAX generation, querying and explanation are disabled by the backend. An approved local configuration is required; no sample answers are substituted.',
    };
  }
  if (!catalog.llmModel?.trim()) {
    return {
      enabled: false,
      label: 'Model not configured',
      reason: 'The backend has not configured an LLM model. Configure the approved local model deployment, then refresh configuration.',
    };
  }
  if (!catalog.liveUntilUtc) {
    return {
      enabled: false,
      label: 'Live window missing',
      reason: 'The backend has not provided an approved live-window end time. Refresh configuration after the local host is configured.',
    };
  }
  if (Date.parse(catalog.liveUntilUtc) <= now) {
    return {
      enabled: false,
      label: 'Live window expired',
      reason: 'The approved live window has expired. No new requests will run. Refresh configuration after a new window is approved outside this UI.',
    };
  }
  if (!catalog.identities.length || !catalog.questions.length) {
    return {
      enabled: false,
      label: 'Catalog incomplete',
      reason: 'The backend must provide at least one demo identity and one prepared question.',
    };
  }
  return {
    enabled: true,
    label: 'Live enabled',
    reason: 'Retrieves real Fabric IQ schema, generates DAX with the LLM, executes through the authenticated broker with engine RLS, then explains only the filtered rows. Only the question is prepared.',
  };
}
