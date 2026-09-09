/**
 * Stable per-agent colour so the Sessions node, the log tab and the [AGENT] tag always
 * agree. Agents are discovered at runtime, so the palette is hashed rather than hardcoded.
 */
const PALETTE: ReadonlyArray<{ text: string; bg: string }> = [
  { text: 'text-acc-blue', bg: 'bg-acc-blue' },
  { text: 'text-acc-mauve', bg: 'bg-acc-mauve' },
  { text: 'text-acc-peach', bg: 'bg-acc-peach' },
  { text: 'text-acc-green', bg: 'bg-acc-green' },
  { text: 'text-acc-yellow', bg: 'bg-acc-yellow' },
  { text: 'text-accent', bg: 'bg-accent' },
];

export function agentColor(agent: string): { text: string; bg: string } {
  let hash = 0;
  for (let i = 0; i < agent.length; i++) {
    hash = (hash * 31 + agent.charCodeAt(i)) | 0;
  }
  return PALETTE[Math.abs(hash) % PALETTE.length];
}
