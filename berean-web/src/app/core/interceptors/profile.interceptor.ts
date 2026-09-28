import { HttpInterceptorFn } from "@angular/common/http";
import { environment } from "../../../environments/environment";

export const PROFILE_HEADER = "X-Berean-Profile";
const STORAGE_KEY = "berean_profileId";

/**
 * Adds the selected profile to every request to our own Resource API, so notes and other
 * personal-data endpoints know whose they are — see PROFILES_AND_SESSIONS_PLAN.md D3. Reads
 * straight from localStorage rather than ProfileService so it works the moment the app boots,
 * before any service has had a chance to load.
 */
export const profileInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith(environment.apiBaseUrl)) return next(req);

  let profileId: string | null = null;
  try {
    profileId = localStorage.getItem(STORAGE_KEY);
  } catch {
    /* private browsing, storage disabled, etc. */
  }
  if (!profileId) return next(req);

  return next(req.clone({ setHeaders: { [PROFILE_HEADER]: profileId } }));
};
