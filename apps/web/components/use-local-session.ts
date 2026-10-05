"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { LocalAuthContext, parseAuthContext } from "../lib/local-ai-workspace";

type Phase = "checking" | "signed-out" | "ready" | "closing";
const channelName = "ai-office-local-session";
const sameScope = (a: LocalAuthContext, b: LocalAuthContext) =>
  a.tenantId === b.tenantId && a.companyId === b.companyId && a.userId === b.userId
  && JSON.stringify([...a.roles].sort()) === JSON.stringify([...b.roles].sort());

// Notifications carry no authority. Only the existing server context endpoint
// can establish identity and permissions; a notification merely discards caches.
export function useLocalSession(companyId: string, clearPrivateState: () => void) {
  const [phase, setPhase] = useState<Phase>("checking");
  const [auth, setAuth] = useState<LocalAuthContext | null>(null);
  const generation = useRef(0);
  const mounted = useRef(false);
  const phaseRef = useRef<Phase>("checking");
  const authRef = useRef<LocalAuthContext | null>(null);
  const requests = useRef(new Set<AbortController>());
  const mutation = useRef<Promise<void> | null>(null);
  const broadcast = useRef<(() => void) | null>(null);

  const isCurrent = useCallback((value: number) => mounted.current && generation.current === value, []);
  const abortRequests = useCallback(() => {
    for (const controller of requests.current) controller.abort();
    requests.current.clear();
  }, []);
  const reset = useCallback((next: Phase) => {
    generation.current += 1;
    abortRequests();
    phaseRef.current = next;
    authRef.current = null;
    if (mounted.current) {
      clearPrivateState();
      setAuth(null);
      setPhase(next);
    }
    return generation.current;
  }, [abortRequests, clearPrivateState]);
  const accept = useCallback((context: LocalAuthContext) => {
    authRef.current = context;
    phaseRef.current = "ready";
    setAuth(context);
    setPhase("ready");
  }, []);

  const request = useCallback(async (value: number, url: string, init?: RequestInit) => {
    if (!isCurrent(value)) throw new Error("Session changed");
    const controller = new AbortController();
    requests.current.add(controller);
    try {
      return await fetch(url, { ...init, signal: controller.signal });
    } finally {
      requests.current.delete(controller);
    }
  }, [isCurrent]);

  const restore = useCallback(async () => {
    if (!mounted.current) return;
    const value = reset("checking");
    // Cookie-mutating responses must settle before a replacement session is
    // accepted, including when company props change while logout is pending.
    await mutation.current;
    if (!isCurrent(value)) return;
    try {
      const response = await request(value, `/api/local/session?companyId=${encodeURIComponent(companyId)}`, { cache: "no-store" });
      const context = response.ok ? parseAuthContext(await response.json()) : null;
      if (!isCurrent(value)) return;
      if (context?.companyId === companyId) accept(context);
      else reset("signed-out");
    } catch {
      if (isCurrent(value)) reset("signed-out");
    }
  }, [accept, companyId, isCurrent, request, reset]);

  const validate = useCallback(async (value: number) => {
    if (!isCurrent(value) || phaseRef.current !== "ready" || !authRef.current) return false;
    try {
      const response = await request(value, `/api/local/session?companyId=${encodeURIComponent(companyId)}`, { cache: "no-store" });
      const context = response.ok ? parseAuthContext(await response.json()) : null;
      if (!isCurrent(value)) return false;
      if (!context || context.companyId !== companyId) {
        reset("signed-out");
        return false;
      }
      if (!sameScope(authRef.current!, context)) {
        reset("checking");
        accept(context);
        return false;
      }
      return true;
    } catch {
      if (isCurrent(value)) reset("signed-out");
      return false;
    }
  }, [accept, companyId, isCurrent, request, reset]);

  const pause = useCallback((value: number, milliseconds: number) => new Promise<boolean>((resolve) => {
    if (!isCurrent(value)) { resolve(false); return; }
    const controller = new AbortController();
    requests.current.add(controller);
    const finish = (current: boolean) => {
      clearTimeout(timer);
      requests.current.delete(controller);
      resolve(current);
    };
    const timer = setTimeout(() => finish(isCurrent(value)), milliseconds);
    controller.signal.addEventListener("abort", () => finish(false), { once: true });
  }), [isCurrent]);

  const ready = useCallback(() => mounted.current && phaseRef.current === "ready" && !mutation.current, []);

  const beginMutation = useCallback((expected: "signed-out" | "ready") => {
    if (!mounted.current || mutation.current || phaseRef.current !== expected) return null;
    let release!: () => void;
    const pending = new Promise<void>((resolve) => { release = resolve; });
    mutation.current = pending;
    const notify = broadcast.current;
    notify?.();
    return () => {
      if (mutation.current !== pending) return;
      mutation.current = null;
      release();
      notify?.();
    };
  }, []);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      generation.current += 1;
      phaseRef.current = "checking";
      authRef.current = null;
      abortRequests();
    };
  }, [abortRequests]);

  useEffect(() => { void restore(); }, [restore]);

  useEffect(() => {
    let channel: BroadcastChannel | null = null;
    const seen = new Set<string>();
    const receive = (payload: unknown) => {
      if (!payload || typeof payload !== "object") return;
      const version = Object.getOwnPropertyDescriptor(payload, "version")?.value;
      const id = Object.getOwnPropertyDescriptor(payload, "id")?.value;
      if (version !== 1 || typeof id !== "string" || id.length > 80 || !id || seen.has(id)) return;
      if (seen.size >= 64) seen.clear();
      seen.add(id);
      void restore();
    };
    try {
      if (typeof BroadcastChannel !== "undefined") {
        channel = new BroadcastChannel(channelName);
        channel.onmessage = (event) => receive(event.data);
      }
    } catch { /* Storage events and focus remain available. */ }
    const storage = (event: StorageEvent) => {
      if (event.key !== channelName || !event.newValue || event.newValue.length > 256) return;
      try { receive(JSON.parse(event.newValue)); } catch { /* Invalid notification. */ }
    };
    const revalidate = () => {
      if (ready()) void validate(generation.current);
      else if (!mutation.current && phaseRef.current !== "checking") void restore();
    };
    const focus = () => { revalidate(); };
    const visibility = () => { if (document.visibilityState === "visible") revalidate(); };
    broadcast.current = () => {
      const payload = { version: 1, id: crypto.randomUUID() };
      try { channel?.postMessage(payload); } catch {
        // A cookie mutation can finish after unmount closed this channel.
        // Notify remaining tabs without retaining the unmounted subscriber.
        let temporary: BroadcastChannel | null = null;
        try {
          temporary = new BroadcastChannel(channelName);
          temporary.postMessage(payload);
        } catch { /* Storage/focus fallback. */ }
        finally { temporary?.close(); }
      }
      try {
        localStorage.setItem(channelName, JSON.stringify(payload));
        localStorage.removeItem(channelName);
      } catch { /* Revalidate on focus when storage is unavailable. */ }
    };
    window.addEventListener("storage", storage);
    window.addEventListener("focus", focus);
    document.addEventListener("visibilitychange", visibility);
    return () => {
      broadcast.current = null;
      channel?.close();
      window.removeEventListener("storage", storage);
      window.removeEventListener("focus", focus);
      document.removeEventListener("visibilitychange", visibility);
    };
  }, [ready, restore, validate]);

  return { phase, auth, generation, isCurrent, reset, request, validate, pause, restore, beginMutation,
    ready };
}
