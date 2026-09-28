// The APIs run on the same machine as this app, on fixed ports. Using the page's own host name
// (instead of "localhost") makes the app work when it is opened from another computer.
const host =
  typeof location !== "undefined" && location.hostname
    ? location.hostname
    : "localhost";

// Over HTTPS — Tailscale in front of the app (MOBILE_PLAN.md phase 4) —
// Chrome won't let this page call the plain-HTTP API ports (mixed content),
// and won't install it as an app at all. Route through the page's own
// origin by path instead; `tailscale serve` forwards /resource and /agent
// to the two APIs. Plain HTTP (desktop, LAN) is unaffected: it still hits
// the fixed ports directly.
const isHttps =
  typeof location !== "undefined" && location.protocol === "https:";
const origin = typeof location !== "undefined" ? location.origin : "";

export const environment = {
  production: false,
  apiBaseUrl: isHttps ? `${origin}/resource` : `http://${host}:5121`,
  agentApiUrl: isHttps ? `${origin}/agent` : `http://${host}:5050`,
};
