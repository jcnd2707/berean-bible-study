const PREFIX = "berean_conversationId";

/**
 * One saved-conversation-id localStorage slot per profile, so a shared PC resumes the right
 * person's last chat instead of one key being overwritten by whoever used the browser last
 * (see PROFILES_AND_SESSIONS_PLAN.md Phase 3).
 */
export function conversationStorageKey(profileId: string | null): string {
  return profileId ? `${PREFIX}:${profileId}` : PREFIX;
}
