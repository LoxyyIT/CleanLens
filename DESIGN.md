# CleanLens desktop design

## Product and visual intent

CleanLens is a Windows desktop tool for reviewing installed applications, disk contents and possible cleanup. People need to recognize paths and measured sizes quickly before choosing an action. The interface should feel like a careful inspection instrument: quiet, legible and exact. Use the existing bright WPF workspace, dark navy window chrome and restrained blue accent. Keep destructive controls distinct from browsing and analysis.

## Runtime source of truth

`src/CleanLens.App/App.xaml` owns the shared desktop colors, brushes, shadows and control styles. Bind application components to these resources rather than creating competing theme palettes.

| Role | Runtime resource | Value |
| --- | --- | --- |
| Window chrome | `Navy` / `NavyBrush` | `#071725` |
| Primary actions and selection | `Blue` / `BlueBrush` | `#315FF4` |
| Scan and secondary emphasis | `Cyan` / `CyanBrush` | `#10B3C7` |
| Positive accents | `Teal` / `TealBrush` | `#099C91` |
| Canvas and cards | `Soft` / `SoftBrush` | `#F0F5FB` |
| Dividers and control outlines | `Line` / `LineBrush` | `#DCE6F1` |
| Quiet supporting copy | `Muted` / `MutedBrush` | `#60738B` |
| Standard text | `Ink` / `InkBrush` | `#101F35` |

These resources flow from `App.xaml` into the window chrome, workspace cards, navigation, search controls and lists. More specific surfaces may use a local semantic status color when the state also has a visible label.

## Typography and layout

- Use Segoe UI for the application and Cascadia Code for full filesystem paths when they benefit from fixed-width alignment.
- Prioritize readable names and sizes in the main list; secondary paths and timestamps can be smaller and muted.
- Keep disk measurements honest: they are logical file lengths. A size indicator compares a result with the completed scan total, not with other rows.
- Present the Defender scan as a bounded security check, clearly separated from full-device antivirus protection. Keep the read-only outcome visible and make UAC approval explicit.
- Preserve the existing grouped workspace, moderately rounded cards and clear column alignment. Reserve the strongest contrast for navigation, the selected row, active scanning and final destructive confirmations.
- Use short, restrained interaction feedback. Preserve normal mouse, keyboard and Windows high-contrast behavior.

## Localization and accessibility

The desktop UI supports English, Italian, Spanish and French through `LocalizationCatalog`. Add new visible labels to all four dictionaries and use the selected culture for numbers and dates. Allow room for translated labels. Keep text alongside status colors and use native, keyboard-operable WPF controls.

Full themes, DPI coverage and an accessibility audit remain planned. Do not describe them as verified.
