// Serves the launcher's files from the private R2 bucket, with limits per IP address:
//  - more than BURST_REQUESTS requests in BURST_SECONDS seconds blocks the IP for BLOCK_MINUTES minutes;
//  - more than DAILY_DOWNLOADS different game files in a UTC day refuses further downloads until the next day
//    (downloading the same file again that day, after a failed or stopped try, doesn't count again).
// Every answer carries the IP's usage so the launcher can warn before the limit: X-Downloads-Used,
// X-Downloads-Limit and X-Downloads-Reset (seconds until the count resets). A 429's JSON body says which limit
// ("limit": "daily" or "burst").
// Only GET and HEAD of manifest.json and files under games/ are answered; the bucket can't be listed or written.
// Files under WITHHELD stay in the bucket for preservation but are never served (the WOLF RPG Editor contest games).
//
// Deployed on the free Workers plan, Cloudflare refuses requests past the plan's daily limit instead of billing,
// so downloads can never cost more than R2's free allowance.

import { DurableObject } from "cloudflare:workers";

const json = (status, body, headers = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...headers },
  });

// Kept in the bucket, never served.
const WITHHELD = ["games/wolf/"];

/** The bucket key a request asks for, or null when it isn't one the launcher uses or one that is withheld. */
function keyOf(url) {
  let path;
  try {
    path = decodeURIComponent(url.pathname);
  } catch {
    return null;
  }
  const parts = path.split("/").filter(Boolean);
  if (parts.some((part) => part === "." || part === ".." || part.includes("\\"))) return null;
  const key = parts.join("/");
  if (key === "manifest.json") return key;
  if (WITHHELD.some((prefix) => key.toLowerCase().startsWith(prefix))) return null;
  if (parts.length >= 2 && parts[0] === "games") return key;
  return null;
}

export default {
  async fetch(request, env) {
    if (request.method !== "GET" && request.method !== "HEAD") {
      return json(405, { error: "Only GET and HEAD are allowed." }, { allow: "GET, HEAD" });
    }
    const url = new URL(request.url);
    // The index website links every download here; crawlers that follow those links would only use up the limits.
    if (url.pathname === "/robots.txt") {
      return new Response("User-agent: *\nDisallow: /\n", { headers: { "content-type": "text/plain; charset=utf-8" } });
    }
    const key = keyOf(url);
    if (key === null) return json(404, { error: "Not found." });

    // The limits come first, so a blocked IP never reaches the bucket. If the limiter can't answer (for example
    // the free plan's Durable Object allowance is used up), downloads are refused rather than served unlimited.
    const ip = request.headers.get("cf-connecting-ip") || "unknown";
    const download = key.startsWith("games/") && request.method === "GET";
    let verdict;
    try {
      const limiter = env.LIMITER.get(env.LIMITER.idFromName(ip));
      verdict = await limiter.check(download ? await fileId(key) : null);
    } catch (error) {
      console.error("limiter failed", error);
      return json(503, { error: "Downloads are unavailable right now. Try again later." }, { "retry-after": "600" });
    }
    const usage = {
      "x-downloads-used": String(verdict.used),
      "x-downloads-limit": String(verdict.max),
      "x-downloads-reset": String(verdict.reset),
    };
    if (!verdict.ok) {
      return json(429, { error: verdict.reason, limit: verdict.limit, used: verdict.used, max: verdict.max },
        { "retry-after": String(verdict.retryAfter), ...usage });
    }

    const object = request.method === "HEAD"
      ? await env.BUCKET.head(key)
      : await env.BUCKET.get(key, { onlyIf: request.headers, range: request.headers });
    if (object === null) return json(404, { error: "Not found." }, usage);

    const headers = new Headers(usage);
    object.writeHttpMetadata(headers);
    headers.set("etag", object.httpEtag);
    headers.set("accept-ranges", "bytes");
    // The manifest changes whenever games are added; game files don't.
    headers.set("cache-control", key === "manifest.json" ? "public, max-age=300" : "public, max-age=86400");

    if (request.method === "HEAD") {
      headers.set("content-length", String(object.size));
      return new Response(null, { headers });
    }
    if (!("body" in object)) return new Response(null, { status: 304, headers }); // If-None-Match matched
    // R2 fills in object.range even when nothing asked for one, so only a request with a Range header gets a 206.
    if (object.range && request.headers.has("range")) {
      const { offset = 0, length = object.size - offset } = object.range;
      headers.set("content-range", `bytes ${offset}-${offset + length - 1}/${object.size}`);
      return new Response(object.body, { status: 206, headers });
    }
    return new Response(object.body, { headers });
  },
};

/** A short id for a bucket key, so a day's list of downloaded files stays small. */
async function fileId(key) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(key));
  return [...new Uint8Array(digest).slice(0, 8)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/**
 * One per IP address (by name). Keeps the burst window in memory and the day's download count and any block in
 * storage, and clears its storage at the end of the day so old IPs don't pile up.
 */
export class IpLimiter extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.dailyDownloads = Number(env.DAILY_DOWNLOADS || 500);
    this.burstRequests = Number(env.BURST_REQUESTS || 30);
    this.burstMs = Number(env.BURST_SECONDS || 10) * 1000;
    this.blockMs = Number(env.BLOCK_MINUTES || 10) * 60 * 1000;
    this.recent = []; // request times in the burst window
  }

  /** file is the downloaded file's id, or null for a request that isn't a game download. */
  async check(file) {
    const now = Date.now();
    const day = new Date(now).toISOString().slice(0, 10);
    const midnight = Date.parse(day + "T00:00:00Z") + 86400000;
    const state = (await this.ctx.storage.get("state")) || { day, files: [], blockedUntil: 0 };
    if (state.day !== day || !Array.isArray(state.files)) Object.assign(state, { day, files: [] });
    const answer = (fields) => ({
      used: state.files.length, max: this.dailyDownloads, reset: Math.ceil((midnight - now) / 1000), ...fields,
    });

    if (state.blockedUntil > now) {
      return answer({ ok: false, limit: "burst", reason: "Too many requests. Try again later.",
        retryAfter: Math.ceil((state.blockedUntil - now) / 1000) });
    }

    this.recent = this.recent.filter((time) => now - time < this.burstMs);
    this.recent.push(now);
    if (this.recent.length > this.burstRequests) {
      state.blockedUntil = now + this.blockMs;
      this.recent = [];
      await this.save(state, now);
      return answer({ ok: false, limit: "burst", reason: "Too many requests. Try again later.",
        retryAfter: Math.ceil(this.blockMs / 1000) });
    }

    if (file !== null && !state.files.includes(file)) {
      if (state.files.length >= this.dailyDownloads) {
        return answer({ ok: false, limit: "daily", reason: "Daily download limit reached. Try again tomorrow.",
          retryAfter: Math.ceil((midnight - now) / 1000) });
      }
      state.files.push(file);
      await this.save(state, now);
    }
    return answer({ ok: true });
  }

  async save(state, now) {
    await this.ctx.storage.put("state", state);
    // Forget this IP once its day and any block are over.
    const end = Math.max(Date.parse(state.day + "T00:00:00Z") + 86400000, state.blockedUntil);
    if ((await this.ctx.storage.getAlarm()) !== end) await this.ctx.storage.setAlarm(end);
  }

  async alarm() {
    await this.ctx.storage.deleteAll();
  }
}
