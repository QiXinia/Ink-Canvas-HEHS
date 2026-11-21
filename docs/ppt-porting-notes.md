# PPT / WPS port: parity and integration

## WinUI 3 presentation fixes, 2026-11-20

The floating toolbar saves its physical desktop position before presentation controls change its size. Fullscreen shows dock it at the bottom centre of the presentation display. Reading View and windowed shows use the visible client area of the PowerPoint window. Exiting restores the saved desktop position, fitted to the available work area. Manual placement during the show, folding, DPI changes and removed displays use the existing window placement path.

PowerPoint events now queue a dispatcher refresh for show begin, page change, show end and window activation. Reading View retains an 80 ms polling fallback. Page replacement uses one document notification inside a presenter scene update. Only consumers of transition snapshots receive cloned transition data. A retained COM identity prevents Office proxy recreation from repeatedly reloading an unchanged page. Temporary rejected COM calls retain the previous snapshot. Navigation avoids activating the slideshow window, and navigation panels are shown only when their visibility changes.

The current build passed 777 domain tests, including regression checks for recreated Office proxies and per-slide ink restoration. Real PowerPoint Reading View checks passed for window discovery, bottom-centred placement, folding, position restoration, page-specific ink and single-frame replacement of 1,200 strokes. One run measured 21.3 ms for external navigation and 39.6 ms for restoring the populated page. Idle ink retained the same presented frame. Earlier fullscreen checks passed for placement, restoration, stable ink and atomic switching, with 38.2 ms external navigation and 56.6 ms page restoration. Later automated fullscreen launches returned PowerPoint error `0x80004005`, also reproduced from PowerShell outside Ink Canvas, so the final fullscreen launch sequence could not be reverified. Mixed-DPI hardware and WPS were not exercised in this round. Reports are under `artifacts/ppt-integration-reading`, `artifacts/ppt-integration-check-13` and `artifacts/ppt-integration-tests`.

Source checked for this port: upstream commit `8b2c95b`, chiefly `Ink Canvas/MainWindow_cs/MW_PPT.cs`, `MW_Timer.cs`, `MW_Settings.cs`, `MW_SettingsToLoad.cs`, `MW_Screenshot.cs`, and `Resources/Settings.cs`. This is an adapter-layer port, not a complete migration of the WPF window behavior: the C# feature layer cannot safely reproduce WPF window actions, settings UI, dialogs, animations, screenshots or legacy ISF files by itself.

## Surface added

- `PptInteropService` performs late-bound Running Object Table attachment to `PowerPoint.Application` and (when enabled) `KWPP.Application`; it maintains the old shell API (`IsAttached`, `TryAttach`, `PollSlideShowState`, `Next`, `Previous`, `ExitSlideShow`) and adds managed snapshots/details, `GoToSlide`, slideshow start modes, hidden-slide restore and manual-advance actions. It is explicitly bound to the creating UI STA thread. The host polls from its dispatcher; COM RCWs are not passed through the public snapshot/events.
- `PptSnapshot` distinguishes editing vs slideshow, presentation/COM identity, window handle, actual slide ID and one-based slide index, slideshow position, and slideshow state. The COM slide ID is used for ink buckets; slide index is used for `GotoSlide`; slideshow position remains separate for custom shows.
- `PptInkSession` coordinates current-screen ink through `CaptureCurrentInk` / `ApplyCurrentInk` callbacks, and emits `InkTransition`, `PresentationNotice`, `ScreenshotRequested`, and `Failed`. It clones strokes at the boundary and does not create, clear, or own an app UI/controller. `IPptInkStore` can be replaced by another managed persistence implementation.
- `PptInkFileStore` stores new-format per-slide ink under `Auto Saved - Presentations/<presentation-name>_<slide-count>` using `.icink` plus `InkCanvas-Ppt.json`; it reads/writes the legacy `Position` hint when possible. The legacy-shaped folder and name/count key are kept, so two different documents with the same presentation name and slide count can still collide, as they could in the WPF implementation.

## UI host integration

Create both services on the WinUI UI thread. Bind them to the existing `InkController`, set options from the host's current settings, call `Poll()` on a UI-dispatcher timer (the WPF interval was one second), and subscribe to the notice/screenshot hooks. Example of the bridge only:

```csharp
var pptInterop = new PptInteropService { AllowWps = appSettings.PowerPoint.IsSupportWPS };
var ppt = new PptInkSession(pptInterop,
    new PptInkFileStore(appSettings.Automation.AutoSavedStrokesLocation))
{
    CaptureCurrentInk = () => inkController.Document.Strokes,
    ApplyCurrentInk = strokes =>
    {
        inkController.Document.Clear();
        inkController.Document.AddRange(strokes);
        inkController.Selection.Clear();
        inkController.History.Clear();
    },
    AutoSavePresentationInk = () => appSettings.PowerPoint.IsAutoSaveStrokesInPowerPoint,
    AutoSavePptScreenshots = () => appSettings.PowerPoint.IsAutoSaveScreenShotInPowerPoint,
    NotifyPreviousPage = () => appSettings.PowerPoint.IsNotifyPreviousPage,
    NotifyHiddenSlides = () => appSettings.PowerPoint.IsNotifyHiddenPage,
    NotifySlideTimings = () => appSettings.PowerPoint.IsNotifyAutoPlayPresentation,
    AutoSaveRoot = () => appSettings.Automation.AutoSavedStrokesLocation,
    MinimumScreenshotStrokeCount = () => appSettings.Automation.MinimumAutomationStrokeNumber,
    UseScreenshotDateFolders = () => appSettings.Automation.IsSaveScreenshotsInDateFolders,
    SaveInkWithScreenshot = () => appSettings.Automation.IsAutoSaveStrokesAtScreenshot,
};

ppt.PresentationNotice += notice =>
{
    // Host decides whether to show a prompt. If accepted:
    if (notice.PreviousSlideIndex is int page) ppt.GoToSlide(page);
    if (notice.OfferRestoreHiddenSlides) ppt.RestoreHiddenSlides();
    if (notice.OfferDisableTimings) ppt.DisableSlideTimings();
};
ppt.ScreenshotRequested += request =>
{
    // Capture desktop pixels and write request.SuggestedPath; optionally save request.Strokes.
};
// On each UI-dispatcher tick: ppt.Poll();
```

`ShellController` now constructs one `PptInkSession` next to `AppServices.Ppt`, binds capture/apply callbacks to `OverlayInk`, polls the session on the existing UI-dispatcher timer, and disposes session then interop on the owner STA. The whiteboard currently has its own page controller; PPT ink stays on the screen-overlay controller. `ApplyCurrentInk` clears the document and history without creating an undoable clear action. `PresentationNotice` is presented as explicit ContentDialog choices; screenshot requests capture screen pixels before host-initiated navigation, encode PNG, and optionally save ink. The old `PollSlideShowState(ref bool)` compatibility method remains in `PptInteropService` for any other host.

The host owns visible effects but this migration now wires the main ones: slideshow mode adds toolbar controls; four independent no-activate edge/bottom navigation windows reproduce the WPF previous/page-number/next surfaces and obey their four visibility settings; counter click switches to mouse mode and opens PowerPoint's slide-navigation pane; finger swipes page; slide ink is switched per stable SlideID; screenshot events are captured and written; resume/hidden/timing notices ask for explicit confirmation. The UI uses WinUI native vector/font assets and current DPI scale rather than copying WPF resource dictionaries/Storyboard animations. Legacy ISF `.icstk/.icart` cannot be decoded by the new `.icink` store and is only detected/preserved.

## Upstream parity and deviations

| Upstream behavior | Port status |
|---|---|
| Find active PowerPoint / WPS COM process; retry from a host poll loop | Late-bound ROT attachment for PowerPoint and `KWPP.Application`; WPS opt-out; explicit UI-STA ownership and managed snapshot-change/failure events. The host owns fallback polling and dispatcher refreshes. The adapter subscribes to show begin, next slide, show end and window activation events. |
| Read active presentation/selected slide, with slideshow-view fallback | Snapshot reads editing view or active slideshow window and reports presentation identity, window, slide ID, index, show position, dimensions, and state. Per-property failures are guarded; details inventory includes hidden and timing properties. |
| Slideshow begin/slide change/end UI behavior | `PptInkSession` reports begin/slide/end transitions; `ShellController` switches toolbar mode, replaces current overlay ink and clears its selection/history at transitions. Board pages are not used as the slideshow ink store. WPF Storyboard timing/folding/color transitions are replaced with WinUI window/layout updates. |
| Existing slideshow control and start/end | Next/previous/end and presentation-settings/first/current slide starts, plus one-based `GoToSlide`. Current-slide start uses the Office `SlideShowFromCurrent` command where available; its fallback temporarily applies a slide range and restores settings. No installer/update actions copied. |
| Cache ink by slide and restore on slide change | Managed per-presentation cache keyed by stable `SlideID`; actual slide index and show position are tracked independently. `InkTransition` emits detached clones; `ApplyCurrentInk` replaces host ink. |
| Load/save `Auto Saved - Presentations/<name>_<count>`; save last page | Same directory naming and `Position` hint, with manifest, current `.icink` format and slide-ID filenames. New code does not read WPF `.icstk`/`.icart` ISF; it flags legacy files in `PresentationNotice` and leaves them untouched. Position stores actual presentation slide index and show position separately, improving custom-show resume over the original's show-position-as-slide-index behavior. |
| Hidden-slide notification and affirmative unhide | Details report hidden slides; `ShellController` shows a ContentDialog and calls `RestoreHiddenSlides()` only after affirmative confirmation. |
| Auto-play/timing detection and affirmative manual-advance action | Reports whether any slide has `AdvanceOnTime` and positive `AdvanceTime`; `ShellController` presents a confirmation and calls `DisableSlideTimings()` only after the user accepts. |
| Automatic PPT screenshot before/around navigation, honoring stroke threshold and date folders | `PptInkSession` emits explicit/navigation/external slide requests. `ShellController` captures screen pixels before host navigation, asynchronously writes PNG to the suggested presentation/position path, and optionally writes `.icink` alongside it. External change uses `InkSlide` to associate the outgoing strokes with the previous slide. |
| WPF settings toggles | All current `PowerPointSettings` values are represented in the grouped WinUI settings page. Navigation panel/counter visibility, WPS opt-out, screenshot/ink saving and notice settings are wired to host behavior. `IsEnableTwoFingerGestureInPresentationMode` and the separate WPF “no clear”/“show stroke” selection semantics remain informational where not yet matched. |
| Original WPF `.icstk`/`.icart` data loading | Not migrated; current app persistence uses `.icink`. The service reports legacy files and never overwrites/deletes them. |

This is a **feature + host-UI PPT/WPS migration**, not a pixel-identical WPF storyboard migration; known differences are listed below.

## Verification and remaining gaps

Per user instruction, PPT/Office/WPS behavioral validation was skipped: no unit tests, PowerPoint/WPS COM attachment run, slideshow run, screenshot capture, or hardware/UI interaction was performed. Only the full Release solution compile was performed (0 warnings/errors). Remaining parity gaps: Office/WPS vendor-version differences in late-bound COM are unverified; existing `.icstk/.icart` WPF ISF archives are detected but not converted; slideshow ink is bound to the screen-overlay controller rather than simultaneously integrated with a blackboard page controller; original WPF Storyboard/folding/canvas-opacity visuals are not pixel-identical. The shell constructs/polls/disposes session and interop on the owning UI STA.
