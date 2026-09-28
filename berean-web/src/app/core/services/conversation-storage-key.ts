const PREFIX = "berean_conversationId";
const READING_POSITION_PREFIX = "berean_readingPosition";

/**
 * One saved-conversation-id localStorage slot per profile, so a shared PC resumes the right
 * person's last chat instead of one key being overwritten by whoever used the browser last
 * (see PROFILES_AND_SESSIONS_PLAN.md Phase 3).
 */
export function conversationStorageKey(profileId: string | null): string {
  return profileId ? `${PREFIX}:${profileId}` : PREFIX;
}

/** Same per-profile pattern as {@link conversationStorageKey}, for the remembered reading position. */
export function readingPositionStorageKey(profileId: string | null): string {
  return profileId ? `${READING_POSITION_PREFIX}:${profileId}` : READING_POSITION_PREFIX;
}
