# Fluent Design User Stories — Per-Page

> **Baseline:** `Keyboard.xaml` is the authoritative reference for Fluent Design patterns in UXTU.
> **Goal:** Every page converges to the Keyboard page's patterns while preserving page-specific functionality.
> **Constraint:** Avoid pure WinUI 3 Gallery "cultism" — the Keyboard page already has personal fixes applied where strict adherence produced poor results.

---

## US-FLD-01: DashboardPage Fluent Design Alignment ✅ **COMPLETE**

**Priority:** P0 (landing page — highest visibility)
**Effort:** Low
**Status:** Done (commits `69bdd0a`, `37b44bc`)
**Current alignment:** 9/10

### Story
As a user opening UXTU, I want the Dashboard page to have the same visual polish and consistency as the Keyboard page, so the app feels cohesive from the first screen.

### Current State
- No Grid rows — single `ScrollViewer` wrapping everything
- 16px horizontal margins (baseline: 24px)
- Title `FontSize="18"` inside scroll area (baseline: 26 outside scroll)
- No subtitle
- Custom controls (`CpuControl`, `GpuControl`, `SystemControl`, `DevicesControl`) in 2×2 Grid
- "Quick Actions" section with custom `QuickActionsControl`
- Raw `TextBlock`, no `ui:TextBlock`

### Acceptance Criteria

- [x] Page uses `Grid` with 3 rows: `Auto` (title), `*` (ScrollViewer), `Auto` (bottom)
- [x] Title row: `StackPanel Margin="24,12,24,8"` with `TextBlock FontSize="26" FontWeight="Medium"` and subtitle
- [x] Subtitle: `Margin="0,8,0,12" FontSize="12" Foreground="{DynamicResource TextFillColorSecondaryBrush}"`
- [x] ScrollViewer in Grid.Row="1" with `Padding="0,0,8,0"` DataTrigger
- [x] Content `StackPanel Margin="24,0,24,18"`
- [x] Page title text: "Dashboard" with subtitle "Monitor your system's performance and hardware status in real time."
- [x] Hardware metrics 2×2 Grid preserved (custom controls are functional, not baseline-breaking)
- [x] "Quick Actions" section header uses `FontSize="20" FontWeight="Medium"` (section header pattern — acceptable deviation)
- [x] `SnapsToDevicePixels="True"`, `UseLayoutRounding="True"`, `ScrollViewer.CanContentScroll="False"` present
- [x] No hardcoded colors

### Notes
- The custom hardware controls (`CpuControl`, `GpuControl`, etc.) are NOT card controls — they're specialized UI widgets. Their internal styling is out of scope for this story.
- The 2×2 Grid layout is functional and should be preserved.
- "Quick Actions" is a section header, not a card title — `FontSize="20"` is acceptable as a deviation from the Body typography.

---

## US-FLD-02: Automations Fluent Design Alignment ✅ **COMPLETE**

**Priority:** P0 (smallest page, cleanest card pattern, quickest win)
**Effort:** Low
**Current alignment:** 9/10

### Story
As a user configuring automation rules, I want the Automations page to match the visual style of the Keyboard page, so the app feels consistent.

### Current State
- Single `StackPanel Margin="18,10,18,18"` — no Grid, no ScrollViewer
- 18px margins (baseline: 24px)
- Title `FontSize="26" FontWeight="Medium"` — correct
- Subtitle present but `Margin="0,4,0,12"` (top 4px vs 8px)
- 5 `CardControl`s with `SymbolIcon` via `Icon="{ui:SymbolIcon}"` shorthand
- Card headers use `TextBlock FontSize="14" FontWeight="Medium"` + subtitle `FontSize="12"`
- Card spacing: first card no margin, subsequent `Margin="0,8,0,0"`
- Page fits without scrolling

### Acceptance Criteria

- [x] Page uses `Grid` with 2 rows: `Auto` (title), `*` (ScrollViewer)
- [x] Title row: `StackPanel Margin="24,12,24,8"`
- [x] Subtitle: `Margin="0,8,0,12"`
- [x] ScrollViewer in Grid.Row="1" with `Padding="0,0,8,0"` DataTrigger
- [x] Content `StackPanel Margin="24,0,24,18"`
- [x] Card icons switch from `SymbolIcon` to `ui:FontIcon` with `Segoe Fluent Icons`, `FontSize="20"`, `Margin="4,0,6,0"`
- [x] Card titles use `ui:TextBlock FontTypography="Body"`
- [x] Card subtitles use `ui:TextBlock FontTypography="Caption" Foreground="{DynamicResource TextFillColorTertiaryBrush}"`
- [x] Card spacing: `Margin="0,0,0,12"` on all cards
- [x] Icon glyph mapping:
  - `BatteryCharge20` → `&#xE897;` (BatteryCharging)
  - `Battery720` → `&#xE97E;` (Battery)
  - `Sleep20` → `&#xE7C5;` (Sleep)
  - `TopSpeed20` → `&#xE7BA;` (Lightning)
- [x] Page still fits without scrolling (no functional regression)

### Notes
- This is the simplest page to fix — 197 lines, well-structured cards
- No ScrollViewer needed if content fits, but having one provides future-proofing
- Icon glyph codes need verification against Segoe Fluent Icons

---

## US-FLD-03: Games Fluent Design Alignment ✅ **COMPLETE**

**Priority:** P1 (high visibility, game library is a core feature)
**Effort:** Low-Medium
**Status:** Done (commit `f92d196`)
**Current alignment:** 9/10

### Story
As a user browsing my game library, I want the Games page to have consistent margins, typography, and spacing matching the rest of the app.

### Current State
- `Grid Margin="18,10,18,10"` → 3 rows
- 18px margins (baseline: 24px)
- Title `FontSize="26" FontWeight="Medium"` — correct
- Subtitle present, `Margin="0,4,0,0"`
- ListBox with custom ItemTemplate using `ui:Card`
- Loading overlay centered on page
- Action buttons (reload, add) in bottom row

### Acceptance Criteria

- [x] Outer Grid margin: `Margin="24,12,24,12"`
- [x] Title StackPanel margin updated to match 24px horizontal
- [x] Subtitle: `Margin="0,8,0,12"`
- [x] ListBox item cards use `Margin="0,0,0,8"` (already correct)
- [x] Action buttons row preserved in Grid.Row="2"
- [x] Loading overlay preserved
- [x] No functional regression to game list display

### Notes
- The ListBox with `ui:Card` ItemTemplate is a unique and functional pattern — NOT a CardControl/CardExpander
- The card items inside the ListBox are NOT page-level cards — they don't need the icon+header pattern
- This page needs minimal changes: just margin and subtitle adjustments
- 207 lines — small page

---

## US-FLD-04: FlydigiCooler Fluent Design Alignment ✅ **COMPLETE**

**Priority:** P1 (always visible in footer navigation)
**Effort:** Medium
**Status:** Done (commit `07eb034`)
**Current alignment:** 9/10

### Story
As a user controlling my Flydigi cooler, I want the page to have consistent margins, typography, and a sticky title that doesn't scroll away.

### Acceptance Criteria

- [x] Page uses `Grid` with centered max-width layout + ScrollViewer
- [x] Title row: `TextBlock FontSize="26" FontWeight="Medium"` outside scroll
- [x] Subtitle: `FontSize="12" Foreground="{DynamicResource TextFillColorSecondaryBrush}"`
- [x] ScrollViewer with `Padding="0,0,8,0"` DataTrigger
- [x] Card icons use `ui:FontIcon` with `Segoe Fluent Icons`
- [x] Card headers use `ui:TextBlock FontTypography="Body"` + `FontTypography="Caption"` subtitle
- [x] Card spacing: `Margin="0,0,0,8"`
- [x] Design-time attributes: `d:DataContext`, `d:Background`, `SnapsToDevicePixels="True"`, `UseLayoutRounding="True"`, `mc:Ignorable="d"`
- [x] Device image and glow effect preserved
- [x] Status panels (Connected/Disconnected) preserved
- [x] `SnackbarPresenter` and adaptive overlay preserved

### Notes
- Uses 36px side margins with MaxWidth="1080" centered layout (deviation from 24px baseline — deliberate for visual polish)
- Glow overlay still uses `#007ACC` (functional effect color, not easily mapped to DynamicResource)
- Status icons use `Green`/`Red` (named colors — appropriate for status indicators)
- 914 lines — grew from 474 due to Fluent Design controls expansion

---

## US-FLD-05: FanControl Fluent Design Alignment ✅ **COMPLETE**

**Priority:** P1 (core feature on XMG laptops)
**Effort:** Medium
**Status:** Done (commit TBD)
**Current alignment:** 9/10

### Story
As a user managing fan curves on my XMG laptop, I want the FanControl page to have consistent typography, spacing, and a sticky title.

### Acceptance Criteria

- [x] Page uses `Grid` with 3 rows: `Auto` (title), `*` (ScrollViewer), `Auto` (bottom bar)
- [x] Title row: `StackPanel Margin="24,12,24,8"` — title moves OUT of ScrollViewer
- [x] Subtitle: `Margin="0,8,0,12"` — "Manage fan speeds and temperature curves for your device."
- [x] ScrollViewer in Grid.Row="1" with `Padding="0,0,8,0"` DataTrigger
- [x] Content `StackPanel Margin="24,0,24,18"`
- [x] Card titles use `ui:TextBlock FontTypography="Body"` (replaced `FontWeight="DemiBold"`)
- [x] Card subtitles use `ui:TextBlock FontTypography="Caption"`
- [x] Card spacing: `Margin="0,0,0,12"` on all cards
- [x] Fan Turbo card: uses `SystemFillColorCriticalBackgroundBrush` (already correct)
- [x] Bottom bar preserved in Grid.Row="2"
- [x] CPU/GPU status cards side-by-side Grid preserved (functional layout)
- [x] Fan curve editors preserved
- [x] `SnackbarPresenter` and adaptive overlay preserved

### Notes
- The CPU/GPU side-by-side layout is functional — preserved the Grid structure
- The bottom bar with Reset Fan State / Restore Auto buttons is functional — preserved
- The Fan Turbo warning card already uses `SystemFillColorCriticalBackgroundBrush` — no hardcoded colors found
- Info Banner and internal CardExpander spacing kept at 8px (internal element spacing, not card-level)
- 516 lines — medium complexity

---

## US-FLD-06: Watercooler Fluent Design Alignment

**Priority:** P2 (conditional feature, similar to FlydigiCooler)
**Effort:** Medium
**Current alignment:** 5/10

### Story
As a user controlling my LCT watercooler, I want the page to match the visual style of the FlydigiCooler page and the Keyboard baseline.

### Current State
- Nearly identical to FlydigiCooler
- No Grid rows, title scrolls
- `FontSize="20"`, `FontWeight="SemiBold"`, no subtitle
- Horizontal icon+title card pattern
- Hardcoded colors
- Missing design-time attributes

### Acceptance Criteria

- [ ] Same structural changes as FlydigiCooler (US-FLD-04)
- [ ] Title: "LCT Watercooler Control", `FontSize="26"`, `FontWeight="Medium"`
- [ ] Subtitle: "Control your LCT watercooler's pump speed, fan RPM, RGB lighting, and device settings."
- [ ] Card icon pattern matches Keyboard baseline
- [ ] Typography uses `ui:TextBlock` with `FontTypography`
- [ ] Hardcoded colors replaced
- [ ] Design-time attributes added
- [ ] Device image and glow effect preserved
- [ ] `ComboBox.ItemTemplate` device list preserved

### Notes
- Can be fixed together with FlydigiCooler since patterns are identical
- 244 lines — smaller than FlydigiCooler

---

## US-FLD-07: Premade Fluent Design Alignment

**Priority:** P2 (unique layout, needs careful redesign)
**Effort:** Medium
**Current alignment:** 5/10

### Story
As a user selecting a premade tuning preset, I want the page to have consistent margins and typography while preserving the unique toggle-button layout.

### Current State
- `Grid Margin="18,10,18,12"` → 3 rows
- 18px margins
- Title `FontSize="26" FontWeight="Medium"` — correct
- Subtitle present, `Margin="0,4,0,0"`
- Split layout: image left, card right
- Bottom bar with 4 toggle buttons in UniformGrid
- Custom `PresetToggleButtonStyle`
- No scrolling needed

### Acceptance Criteria

- [ ] Outer Grid margin: `Margin="24,12,24,12"`
- [ ] Title StackPanel: `Margin="24,12,24,8"`
- [ ] Subtitle: `Margin="0,8,0,12"`
- [ ] Split layout (image + card) preserved in Grid.Row="1"
- [ ] Preset detail card typography updated:
  - Preset name: `ui:TextBlock FontTypography="BodyStrong"` (or keep `FontSize="20" FontWeight="DemiBold"` as acceptable deviation)
  - Description: `ui:TextBlock FontTypography="Body"`
- [ ] Bottom toggle bar preserved in Grid.Row="2"
- [ ] `PresetToggleButtonStyle` preserved (functional, not baseline-breaking)
- [ ] Badge styling preserved

### Notes
- The toggle button bar at the bottom is a unique and functional pattern — preserve it
- The split image+card layout is deliberate — don't change to card pattern
- This page has no scrolling — different page model than baseline
- 195 lines — small page

---

## US-FLD-08: OverlaySettingsPage Fluent Design Alignment

**Priority:** P2 (uses wrong card type)
**Effort:** Medium
**Current alignment:** 4/10

### Story
As a user configuring the game overlay, I want the page to use consistent card patterns and typography.

### Current State
- `Grid` → inner `Grid` → 3 rows: title, scroll, bottom bar
- 18px title margins, 15px content margins
- Uses `ui:Card` (not CardControl/CardExpander)
- No card icons
- Bottom bar with 3 action buttons
- `helpers:MouseWheelScrollBehavior.IsEnabled="True"`

### Acceptance Criteria

- [ ] Remove double-nested Grid — single Grid with 3 rows
- [ ] Title row: `StackPanel Margin="24,12,24,8"`
- [ ] Subtitle: `Margin="0,8,0,12"`
- [ ] ScrollViewer in Grid.Row="1" with `Padding="0,0,8,0"` DataTrigger
- [ ] Content `StackPanel Margin="24,0,24,18"`
- [ ] `ui:Card` elements converted to `ui:CardControl` with `.Icon` property
- [ ] Card headers use `ui:TextBlock FontTypography="Body"` + `FontTypography="Caption"`
- [ ] Card spacing: `Margin="0,0,0,12"`
- [ ] Icon assignment per card:
  - "Overlay behaviour" → `&#xE775;` (Eye)
  - "Overlay contents" → `&#xE9CA;` (Display)
- [ ] Bottom bar preserved in Grid.Row="2"
- [ ] `MouseWheelScrollBehavior` preserved
- [ ] Hotkey TextBox pattern preserved

### Notes
- Converting `ui:Card` to `ui:CardControl` may require restructuring the header/content layout
- The hotkey TextBox pattern is functional — preserve
- 266 lines — medium page

---

## US-FLD-09: SettingsPage Fluent Design Alignment

**Priority:** P3 (well-structured, has section headers)
**Effort:** Medium-High
**Current alignment:** 5/10

### Story
As a user configuring application settings, I want the page to have consistent typography, spacing, and card patterns.

### Current State
- `Grid` → 2 rows: title, scroll
- 18px margins
- Section-level headers (`FontSize="20"`) between card groups
- Mix of CardExpander (info) and Card (settings)
- Inconsistent card spacing (8, 10, 12px)
- CardExpander headers are plain TextBlock (no Grid subtitle)

### Acceptance Criteria

- [ ] Title row: `StackPanel Margin="24,12,24,8"`
- [ ] Subtitle: `Margin="0,8,0,12"`
- [ ] ScrollViewer with `Padding="0,0,8,0"` DataTrigger
- [ ] Content `StackPanel Margin="24,0,24,18"`
- [ ] Section headers use `ui:TextBlock FontTypography="BodyStrong"` (replace `FontSize="20" FontWeight="Medium"`)
- [ ] CardExpander headers use `ui:TextBlock FontTypography="Body"`
- [ ] Card spacing: `Margin="0,0,0,12"` (unify from 8/10/12)
- [ ] `ui:Card` elements converted to `ui:CardControl` with `.Icon` property
- [ ] Icon assignment per card:
  - "Application behaviour" → `&#xE7BA;` (Lightning)
  - "Preset backup" → `&#xE7C4;` (Save)
  - "Diagnostics" → `&#xE7BA;` (Bug)
- [ ] System Info CardExpanders use icon+header pattern
- [ ] `EnumToBooleanConverter` preserved
- [ ] `ui:InfoBar` preserved

### Notes
- Section headers ("System Info", "Settings") are a useful pattern — preserve as `BodyStrong`
- The mix of CardExpander (info) and Card (settings) is functional — convert Cards to CardControls
- Duplicate System Info section (also in SystemInfo page) — consider whether to keep or remove
- 502 lines — medium-large page

---

## US-FLD-10: SystemInfo Fluent Design Alignment

**Priority:** P3 (data-dense, large RAM timings grid)
**Effort:** Medium
**Current alignment:** 5/10

### Story
As a user reviewing system information, I want the page to have consistent margins, typography, and card patterns.

### Current State
- `Grid` → 2 rows: title, scroll
- 18px title margins, 15px scroll/content margins
- CardExpander headers are plain TextBlock (no subtitle)
- Massive RAM timings grid (32 rows × 4 columns)

### Acceptance Criteria

- [ ] Title row: `StackPanel Margin="24,12,24,8"`
- [ ] Subtitle: `Margin="0,8,0,12"`
- [ ] ScrollViewer with `Padding="0,0,8,0"` DataTrigger
- [ ] Content `StackPanel Margin="24,0,24,18"`
- [ ] CardExpander headers use `ui:TextBlock FontTypography="Body"`
- [ ] Card spacing: `Margin="0,0,0,12"`
- [ ] Icon assignment:
  - "Device Information" → `&#xE960;` (Desktop)
  - "Processor Information" → `&#xEEA1;` (Processor)
  - "Memory Information" → `&#xE964;` (Ram)
  - "Battery Information" → `&#xE97E;` (Battery)
- [ ] RAM timings grid preserved (data display, not baseline-breaking)
- [ ] Data values use `TextFillColorTertiaryBrush`

### Notes
- The RAM timings grid is a data display widget — not a card pattern
- This page duplicates content from SettingsPage System Info section
- 460 lines — medium page

---

## US-FLD-11: CustomPresets Fluent Design Alignment

**Priority:** P3 (largest page — 2556 lines)
**Effort:** High
**Current alignment:** 4/10

### Story
As a user creating custom tuning presets, I want the page to have consistent typography, spacing, and a sticky title.

### Current State
- `Grid` → inner `Grid` → 4 rows: title, preset bar, scroll, bottom bar
- 18px title margins, 15px content margins
- Preset bar (DockPanel) between title and scroll
- Non-standard font sizes: 17, 13
- 2556 lines — largest page

### Acceptance Criteria

- [ ] Remove double-nested Grid — single Grid with 4 rows
- [ ] Title row: `StackPanel Margin="24,12,24,8"`
- [ ] Subtitle: `Margin="0,8,0,12"`
- [ ] Preset bar row preserved (DockPanel with ComboBox + TextBox)
- [ ] ScrollViewer with `Padding="0,0,8,0"` DataTrigger
- [ ] Content `StackPanel Margin="24,0,24,18"`
- [ ] CardExpander headers use `ui:TextBlock FontTypography="Body"` + `FontTypography="Caption"`
- [ ] Card spacing: `Margin="0,0,0,12"`
- [ ] Non-standard font sizes (17, 13) mapped to typography system
- [ ] Bottom bar with 5 action buttons preserved
- [ ] `SnapsToDevicePixels="True"`, `UseLayoutRounding="True"` added

### Notes
- This is the largest page — break into sub-tasks if needed
- The preset bar is a functional pattern — preserve
- Bottom bar with Undo/Reload/Delete/Save/Apply buttons — preserve
- Consider splitting into multiple commits given the size

---

## US-FLD-12: Adaptive Fluent Design Alignment 🔄 **IN PROGRESS**

**Priority:** P3 (complex page — 1558 lines)
**Effort:** High
**Status:** Flydigi Cooler card redesigned (commit `408229a`). Page-level structure still needs work.
**Current alignment:** 5/10

### Story
As a user configuring Adaptive Mode, I want the page to have consistent typography, spacing, and a subtitle.

### Current State
- `Grid` → inner `Grid Margin="15,0,0,0"` → 5 rows
- 15px left-only margin
- No subtitle
- Non-standard font sizes: 17, 13
- Complex nested CardExpanders with toggles
- Flydigi Cooler card redesigned with Fluent Design controls (shared `FlydigiIconElement`, curve editor, color picker)
- 1558 lines — second largest page (grew from 1349 due to Flydigi card expansion)

### Acceptance Criteria

- [ ] Remove double-nested Grid — single Grid with 5 rows
- [ ] Title row: `StackPanel Margin="24,12,24,8"`
- [ ] Subtitle: `Margin="0,8,0,12"` — "Automatically adjust performance settings based on the active game or application."
- [ ] Preset bar row preserved (DockPanel with ComboBox + buttons)
- [ ] Notes row preserved
- [ ] ScrollViewer with `Padding="0,0,8,0"` DataTrigger (currently 12px)
- [ ] Content `StackPanel Margin="24,0,24,18"`
- [ ] CardExpander headers use `ui:TextBlock FontTypography="Body"` + `FontTypography="Caption"`
- [ ] Card spacing: `Margin="0,0,0,12"`
- [ ] Non-standard font sizes (17, 13) mapped to typography system
- [x] Flydigi Cooler card redesigned with Fluent Design controls (commit `408229a`)
- [ ] Bottom bar with polling rate + Auto switch + Start/Stop preserved
- [ ] Nested CardExpanders with toggles preserved

### Notes
- Second largest page — break into sub-tasks if needed
- The preset bar and bottom bar are functional patterns — preserve
- Nested CardExpanders are complex — don't restructure, just update typography
- Flydigi Cooler card is complete; remaining work is page-level structure + typography on other cards
- Consider splitting into multiple commits

---

## US-FLD-13: DataPage Fluent Design Alignment

**Priority:** P4 (minimal placeholder page)
**Effort:** Low
**Current alignment:** N/A

### Story
As a user, I want the Data page to have a title and subtitle matching the rest of the app, even though it's a placeholder.

### Current State
- No title, no subtitle
- Just `ui:VirtualizingItemsControl` with color buttons
- `Margin="18"` on ItemsControl

### Acceptance Criteria

- [ ] Page uses `Grid` with 2 rows: `Auto` (title), `*` (content)
- [ ] Title row: `StackPanel Margin="24,12,24,8"`
- [ ] Subtitle: `Margin="0,8,0,12"`
- [ ] ItemsControl margin: `Margin="24,0,24,18"`
- [ ] Page title: "Data" with subtitle "Debug and testing page."

### Notes
- Minimal page — quick fix
- Consider whether this page should even be in production navigation

---

## Cross-Cutting Concerns

### Typography Migration Map

| Current hardcoded size | Baseline equivalent | Notes |
|---|---|---|
| `FontSize="26" FontWeight="Medium"` | Page title (keep as-is) | Baseline uses raw TextBlock for page title |
| `FontSize="20" FontWeight="Medium"` | `ui:TextBlock FontTypography="BodyStrong"` | Section headers |
| `FontSize="17" FontWeight="Medium"` | `ui:TextBlock FontTypography="BodyStrong"` | Card section titles |
| `FontSize="14" FontWeight="Medium"` | `ui:TextBlock FontTypography="Body"` | Card titles |
| `FontSize="14"` (no weight) | `ui:TextBlock FontTypography="Body"` | Card titles |
| `FontSize="13"` | `ui:TextBlock FontTypography="Body"` | Setting labels — non-standard, map to Body |
| `FontSize="12"` | `ui:TextBlock FontTypography="Caption"` | Subtitles, helper text |
| `FontWeight="DemiBold"` | `ui:TextBlock FontTypography="BodyStrong"` | Card titles — FanControl |
| `FontWeight="SemiBold"` | `FontWeight="Medium"` | Page titles — FlydigiCooler, Watercooler |

### Icon Migration Map

| Current `SymbolIcon` | Baseline `FontIcon` glyph | Notes |
|---|---|---|
| `BatteryCharge20` | `&#xE897;` (BatteryCharging) | Verify glyph |
| `Battery720` | `&#xE97E;` (Battery) | Verify glyph |
| `Sleep20` | `&#xE7C5;` (Sleep) | Verify glyph |
| `TopSpeed20` | `&#xE7BA;` (Lightning) | Verify glyph |
| `Temperature20` | `&#xE7C3;` (Temp) | Verify glyph |
| `Desktop20` | `&#xE960;` (Desktop) | Verify glyph |
| `DeveloperBoard20` | `&#xEEA1;` (Processor) | Verify glyph |
| `Ram20` | `&#xE964;` (Ram) | Verify glyph |
| `Power20` | `&#xE7BA;` (Power) | Verify glyph |
| `Eye20` | `&#xE775;` (Eye) | Verify glyph |
| `ArrowSync20` | `&#xE72C;` (ArrowSync) | For buttons, not cards |
| `Save20` | `&#xE74E;` (Save) | For buttons, not cards |
| `Delete20` | `&#xE74D;` (Delete) | For buttons, not cards |
| `Play20` | `&#xE768;` (Play) | For buttons, not cards |
| `Color24` | `&#xE794;` (Fluent/Color) | Verify glyph |
| `Settings20` | `&#xE713;` (Settings) | Verify glyph |
| `Book20` | `&#xE77C;` (Book) | Verify glyph |
| `Pulse20` | `&#xEEA1;` (Processor) | Verify glyph |
| `Image24` | `&#xE7C1;` (Image) | Verify glyph |
| `PresenceAvailable10` | `&#xE735;` (CheckMark) | Verify glyph |
| `LeafTwo20` | `&#xE772;` (Leaf) | For toggle buttons |
| `Scales20` | `&#xF140;` (Balance) | For toggle buttons |
| `Gauge20` | `&#xE8BB;` (Gauge) | For toggle buttons |
| `FastAcceleration20` | `&#xECAD;` (Lightning) | For toggle buttons |

> **Note:** All glyph codes need verification against the actual Segoe Fluent Icons font available in the WPFUI package. The `SymbolIcon` names map to WinUI symbols which may not have 1:1 glyph code equivalents in Segoe Fluent Icons.

### Scrollbar Compensation Pattern

The baseline Keyboard page uses:
```xml
<ScrollViewer.Style>
    <Style TargetType="{x:Type ScrollViewer}" BasedOn="{StaticResource {x:Type ScrollViewer}}">
        <Setter Property="Padding" Value="0" />
        <Style.Triggers>
            <DataTrigger Binding="{Binding ComputedVerticalScrollBarVisibility, RelativeSource={RelativeSource Self}}"
                         Value="Visible">
                <Setter Property="Padding" Value="0,0,8,0" />
            </DataTrigger>
        </Style.Triggers>
    </Style>
</ScrollViewer.Style>
```

All pages should use this exact pattern. Current deviations:
- FanControl, Adaptive, CustomPresets, SystemInfo: 12px (change to 8px)
- FlydigiCooler, Watercooler, OverlaySettingsPage, SettingsPage, DashboardPage, Games: missing (add)
