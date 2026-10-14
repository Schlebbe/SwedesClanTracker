import { useEffect, useRef, useState } from "react";
import { isAbortError } from "../data/apiClient";

const cache = new Map();
const CACHE_TTL = 30_000;

export function getCached(key) {
  const item = cache.get(key);
  return item && Date.now() - item.at < CACHE_TTL ? item.data : null;
}

export function clearConsoleCache() {
  cache.clear();
}

export function useConsoleRequest(key, loader, enabled = true) {
  const cached = getCached(key);
  const loaderRef = useRef(loader);
  loaderRef.current = loader;
  const [state, setState] = useState(() => ({
    loading: enabled && !cached,
    data: cached,
    error: null,
    stale: false,
  }));

  useEffect(() => {
    if (!enabled) {
      setState({ loading: false, data: null, error: null, stale: false });
      return undefined;
    }

    const controller = new AbortController();
    const known = getCached(key);
    setState((previous) => ({
      ...previous,
      loading: !known,
      data: known ?? previous.data,
      error: null,
    }));

    loaderRef.current(controller.signal)
      .then((data) => {
        if (controller.signal.aborted) return;
        cache.set(key, { at: Date.now(), data });
        setState({ loading: false, data, error: null, stale: false });
      })
      .catch((error) => {
        if (controller.signal.aborted || isAbortError(error)) return;
        setState((previous) => ({
          loading: false,
          data: previous.data ?? null,
          error,
          stale: Boolean(previous.data),
        }));
      });

    return () => controller.abort();
  }, [enabled, key]);

  return state;
}
