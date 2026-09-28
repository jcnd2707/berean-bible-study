export interface StoppableMessage {
  role: "user" | "agent";
  text: string;
  streaming?: boolean;
  stopped?: boolean;
}

export interface StoppedMessageResult<T extends StoppableMessage> {
  messages: T[];
  /** The question that was stopped, if there was a preceding one — put back in the input (D11). */
  stoppedQuestion: string | null;
}

/**
 * Turns the currently-streaming agent message (if any) into a stopped one: `streaming: false,
 * stopped: true`. A no-op (same array, null question) if nothing was streaming.
 */
export function applyStoppedMessage<T extends StoppableMessage>(messages: T[]): StoppedMessageResult<T> {
  const last = messages[messages.length - 1];
  if (!(last?.role === "agent" && last.streaming)) {
    return { messages, stoppedQuestion: null };
  }

  const prev = messages[messages.length - 2];
  const stoppedQuestion = prev?.role === "user" ? prev.text : null;
  const stopped: T = { ...last, streaming: false, stopped: true };

  return { messages: [...messages.slice(0, -1), stopped], stoppedQuestion };
}
