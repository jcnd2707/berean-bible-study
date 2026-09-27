// Pure breakpoint logic, kept separate from LayoutService so it can be unit
// tested without the Angular test bed (see tests/layout-classification.test.js).
//
// Classified by the viewport's shorter side, not raw width — width alone
// can't separate a phone from a tablet, because a phone rotated to
// landscape gets *wider* than a tablet held in portrait. See MOBILE_PLAN.md
// §1–2 for the measurements this is based on (phone 411×789, tablet
// 533×752).
export type Layout = "phone" | "tablet" | "desktop";

export const PHONE_MAX_SHORT_SIDE = 480;
export const DESKTOP_MIN_WIDTH = 1200;

export function classifyLayout(width: number, height: number): Layout {
  if (width >= DESKTOP_MIN_WIDTH) return "desktop";
  const shortSide = Math.min(width, height);
  return shortSide < PHONE_MAX_SHORT_SIDE ? "phone" : "tablet";
}
