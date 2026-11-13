# Reference fidelity and DPI implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Bring the layered WPF home and assistant toward the two supplied references and make compact/DPI/monitor recovery repeatably testable.
**Architecture:** Keep native WPF controls, current view models and input gate. Add reusable decorative drawing controls and local keyed styles; keep opaque reading surfaces under text, reserve gradients/highlights for edges. Add a pure physical-monitor geometry resolver and a native monitor adapter used by explicit window positioning and passive bounds recovery without activation.
**Tech Stack:** Existing .NET10 WPF, xUnit; no added dependencies.
**Spec:** docs/parallel/furina-vnext/FURINA_UI_AI_BRANCH_PLAN.md plus coordinator 2026-11-13 reference/DPI assignment.

## Global constraints
- Base85977de; B branch feat/furina-reference-dpi in existing B worktree. A assets delivered separately in A worktree then consumed.
- Do not modify App/project/DB/build/acceptance gates or MainViewModel run submission; I owns partial declaration and ExecutionPlans partial.
- No actual Show, Smoke, activation, monitor mutation or games. Q coordinates live desktop only.
- Real texts/commands/progress; no screenshot hotspots or fake success/progress/version.
- Preserve external art/GIF precedence and optional fallback.
- Final code review at most first+postfix DeepSeek for this new task; previous task reviews not reused.
- Visual acceptance requires Q evidence >=85 total and >=70% per category; self-assessment is not acceptance.

## Actual reference gaps and chosen approach
Compared both reference PNGs against Q e34c104 home/assistant-open screenshots. Current character occupies~300x540 starting far below header; reference character is~450x740 and anchors left foreground. Current plain white rounded panels lack icy edge highlights, narrow golden trim, crown/lozenge title plaques. Reference center has compact overview and three rows plus history/progress fully visible; current content overflows lower viewport. Current game banners are landscapes only. Execute button is plain concentric circles. Water is cropped to12DIP. Assistant is a form-like light rectangle with a small flat header, same plain bubble for every speaker and a detached send row.

Use existing WPF composition (chosen by prior approved plan and explicit autonomous instruction), not WebView replacement nor painted screenshot. Decorative crown/plaque/wave line geometry is code-native; new raster game character art is generated separately in A. Palette: deep#132B74, royal#315DE0, ice#E7EEFF, pearl#F6FAFF, gold#D7BC79, text#142651. Microsoft YaHei UI for readable body; Palatino/Georgia italic brand. Aim nav176, character~340 with controlled visual overflow into noninteractive margins, center~750, controls~280 at1600. Compact rail72, hide character at<1280; summaries independent of1280 threshold based on available middle width. Character never covers actionable controls. No new action without actual command.

## Review focus
- At1280 four summary cells must not clip; longer localized text wraps and commands remain visible.
- 1920x1080 at100/125/150/200% means decreasing DIP work area; screen resolution is not window client area.
- Negative monitor coordinates and differing DPI origins must not be converted by globally dividing coordinates; use physical screen rectangles and per-monitor relative offsets.
- Display removal/workarea shrink must recover bounds without Show/Activate/acquiring chat input; pause does not grant permission.
- Non-solid decoration must not hide contrast failures; all reading areas keep explicit solid surfaces.

## Tasks
- [x] A: regenerate three independent text-free character banners; inspect and record hashes in A increment. Preserve existing filenames/override resolution, no reference edits.
- [x] B1: code-native crown/plaque/glass-edge/wave/execute ornament, home density and role placement, independent summary breakpoint. Extend offscreen checks for1280 and readable long captions; run existing input/confirmation tests.
- [x] B2: assistant header/side-avatar message cards and integrated input/shortcuts, preserving all gate events and controls. Test minimum viewport and long Chinese at360x460, normal460x720 and optional512x720.
- [x] B3: pure monitor resolver tests RED (negative origins, mixedDPI, removed monitor, tiny workarea, repeat clamping), minimal native monitor adapter and window hooks GREEN. No actual monitor queries required by unit tests.
- [x] Evidence: render actual content offscreen at100/125/150/200 pixel densities and representative logical workareas; validate controls/contrast without Show; identify images as offscreen, bind commit/hash. Tests appropriate to changes, no smoke.
- [x] Fresh whole-branch review, then DeepSeek actual diff/context, fix confirmed blockers and use only one final re-review if needed. New draft PR to integration; final visual score left to Q, not declared passed absent evidence.

- [ ] Q actual desktop/DPI and independent visual85/70 acceptance: pending, not self-certified.

