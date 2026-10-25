/**
 * The reactions the audience can send. To add a custom image, drop a PNG in
 * `public/reactions/` and add e.g. `{ id: 'logo', label: 'Logo', image: '/reactions/logo.png' }`.
 * The desktop overlay downloads images by URL; emojis use its bundled artwork.
 */
export interface Reaction {
  id: string;
  label: string;
  emoji?: string;
  image?: string;
}

export const REACTIONS: readonly Reaction[] = [
  { id: 'grin', label: 'Grinning', emoji: '😁' },
  { id: 'joy', label: 'Laughing', emoji: '😂' },
  { id: 'thumbs-up', label: 'Thumbs up', emoji: '👍🏽' },
  { id: 'thumbs-down', label: 'Thumbs down', emoji: '👎🏽' },
  { id: 'poop', label: 'Poop', emoji: '💩' },
  { id: 'question', label: 'Question', emoji: '❓' },
];
// 🖐🏽 isn't a reaction any more: it's the "Ask" button (see questions.ts).

const byId = new Map(REACTIONS.map((r) => [r.id, r]));

export function findReaction(id: unknown): Reaction | undefined {
  return typeof id === 'string' ? byId.get(id) : undefined;
}
