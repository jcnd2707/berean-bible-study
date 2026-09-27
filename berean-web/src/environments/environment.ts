// The APIs run on the same machine as this app, on fixed ports. Using the page's own host name
// (instead of "localhost") makes the app work when it is opened from another computer.
const host =
  typeof location !== "undefined" && location.hostname
    ? location.hostname
    : "localhost";

export const environment = {
  production: false,
  apiBaseUrl: `http://${host}:5121`,
  agentApiUrl: `http://${host}:5050`,
};
