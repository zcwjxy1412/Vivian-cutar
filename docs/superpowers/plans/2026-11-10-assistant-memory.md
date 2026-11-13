# Assistant memory implementation plan

Goal: Preserve complete conversation turns and rotate conversations safely after 20 complete turns or a conservative input budget.
Architecture: AssistantSession owns complete turns; an encrypted atomic local store persists active and archived snapshots, scoped to the configured endpoint and model. The floating assistant exposes a fresh conversation and read-only archives; authorization remains outside memory.
Tech stack: Existing C#/.NET 10, WPF, Windows DPAPI. No dependencies or schema changes.

- [ ] Confirm failing regression in Windows CI for premature four-turn truncation.
- [ ] Add encrypted conversation snapshot storage and failure-safe atomic replacement. Test persistence, corruption refusal, endpoint isolation and archival.
- [ ] Add summary preparation without tools, bounded input/output, complete-turn rollover, no history loss on failure. Test 20-turn boundary, cancellation and retry.
- [ ] Connect UI with read-only archive viewing and fresh conversation presentation. Revoke prior proposals on transition; no authorization is carried in summaries.
- [ ] Review changes and run full existing Windows validation. Do not merge or deploy.

Safety: Keep source transcripts encrypted locally; omit model reasoning from persistent records. Never write credentials into snapshots. Summaries are untrusted historical data, never instructions or authorizations. Failed/canceled turns are not replayed into model context. A failed durable write blocks the next request rather than discarding an answer. Endpoint changes create a distinct store and never send old history to a new service. Archive writes and active pointer changes are one snapshot replacement.

Verification uses existing Windows CI because local .NET/Windows tools are unavailable. Isolated branch feature/assistant-session-memory-20261110; base 36bfd11710722fb427d0d82481df2037b6677a44. No real model credentials or game execution.

Implementation interfaces:
- ConversationState: Version, Active (ConversationRecord), Archived (List<ConversationRecord>).
- ConversationRecord: Id, StartedAt, Summary, List<JsonArray> Turns. Original message/tool arrays are retained with reasoning fields removed; archive viewers expose text only.
- ConversationStore.Load/Save: DPAPI encrypted atomic snapshot, compare expected revision under exclusive file lock; reject corruption and conflicts without replacing evidence.
- AssistantSession exposes ConversationId, MemoryNotice, history transcript and archive snapshots. Request lifecycle flushes dirty completed turn before any subsequent model call; rollover never publishes stale executable cards.

Review focus:
- Disk full / failed write must preserve completed in-memory answer and previous durable file; next request retries persistence before sending.
- Summary cancellation or invalid response never mutates active session or archive.
- Long tool outputs and escaped Unicode are included in conservative request accounting; missing token metadata is never presented as exact.
- Endpoint/account change must neither replay old transcript nor upload old summary.
- Restored/archived model text is data only; no cards or execution grants are reconstructed.
